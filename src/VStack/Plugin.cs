using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Hook;

namespace VStack;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.originera.vstack";
    public const string PluginName = "VStack";
    public const string PluginVersion = "1.0.0";
    public const float DefaultMultiplier = 1000.0f;
    public const float MinimumMultiplier = 0.01f;

    // SettingsClamp::Half ultimately stores the value as a binary16/half value.
    // 65504 is the largest finite positive value representable by IEEE 754 binary16.
    public const float MaximumMultiplier = 65504.0f;

    private const string TargetSettingName = "InventoryStacksModifier";
    private const uint ImageScnMemExecute = 0x20000000;
    private const uint PageExecuteReadWrite = 0x40;

    // V Rising's generated InventoryBuffer snapshot serializer/deserializer currently
    // bounds Amount and MaxAmountOverride to 0x0FFF (4095) on the wire even though
    // both fields are Int32 in the ECS/snapshot structures. VStack raises only these
    // InventoryBuffer wire bounds to the positive Int32 range.
    //
    // IMPORTANT: this changes the number of bits used by this generated snapshot
    // field. The same VStack build must therefore be present on the server and every
    // connecting client while this patch is active.
    private const int VanillaInventoryWireMaximum = 0x00000FFF;
    private const int ExtendedInventoryWireMaximum = int.MaxValue;

    // Current V Rising SettingsClamp::Half signature.
    // Wildcards are represented by 0x00 in Pattern and false in PatternMask.
    private static readonly byte[] Pattern =
    {
        0x40, 0x57, 0x48, 0x83, 0xEC, 0x60, 0x80, 0x3D,
        0x00, 0x00, 0x00, 0x00, 0x00,
        0x49, 0x8B
    };

    private static readonly bool[] PatternMask =
    {
        true, true, true, true, true, true, true, true,
        false, false, false, false, false,
        true, true
    };

    // Instruction sequence inside the generated InventoryBuffer serializers.
    // Current V Rising builds contain TWO generated implementations with the same
    // InventoryBuffer payload layout. They differ only in one stack-local offset
    // ([rsp+0x30] versus [rsp+0x38]). Both must be patched; the live player
    // inventory can use the second implementation.
    //
    // The first 0x0FFF immediate is loaded into r13d and then reused for both
    // InventoryBuffer.Amount and InventoryBuffer.MaxAmountOverride serialization.
    private static readonly byte[] InventorySerializerPattern =
    {
        0x44, 0x8B, 0x64, 0x24, 0x30,
        0x41, 0xBD, 0xFF, 0x0F, 0x00, 0x00,
        0x41, 0x8B, 0x47, 0x04,
        0x41, 0x39, 0x06,
        0x0F, 0x8E,
        0x00, 0x00, 0x00, 0x00,
        0x8B, 0x07,
        0x41, 0x3B, 0xC5,
        0x0F, 0x10, 0x07,
        0x41, 0x0F, 0x4F, 0xC5,
        0xF2, 0x0F, 0x10, 0x4F, 0x10
    };

    private static readonly bool[] InventorySerializerPatternMask =
    {
        true, true, true, true, false, // 0x30 or 0x38 stack-local offset
        true, true, true, true, true, true,
        true, true, true, true,
        true, true, true,
        true, true,
        false, false, false, false,
        true, true,
        true, true, true,
        true, true, true,
        true, true, true, true,
        true, true, true, true, true
    };

    // Generated InventoryBuffer deserializer call site:
    //   mov r9, [...]
    //   xor edx, edx
    //   mov r8d, 0x0FFF
    //   mov rcx, rdi
    //   call <bounded integer reader>
    //
    // There are exactly two matching sites in the local InventoryBuffer deserializer
    // region: one for Amount and one for MaxAmountOverride.
    private static readonly byte[] InventoryDeserializerBoundPattern =
    {
        0x4C, 0x8B, 0x0D,
        0x00, 0x00, 0x00, 0x00,
        0x33, 0xD2,
        0x41, 0xB8, 0xFF, 0x0F, 0x00, 0x00,
        0x48, 0x8B, 0xCF,
        0xE8,
        0x00, 0x00, 0x00, 0x00
    };

    private static readonly bool[] InventoryDeserializerBoundPatternMask =
    {
        true, true, true,
        false, false, false, false,
        true, true,
        true, true, true, true, true, true,
        true, true, true,
        true,
        false, false, false, false
    };

    private const int ExpectedInventorySerializerImplementations = 2;
    private const int SerializerMaximumImmediateOffset = 7;
    private const int SerializerWriterCallOffset = 0x4F;
    private const int DeserializerMaximumImmediateOffset = 11;
    private const int DeserializerReaderCallOffset = 18;
    private const int DeserializerSearchStartOffset = 0x900;
    private const int DeserializerSearchLength = 0x300;

    // Temporary diagnostic hook for ItemGridSelectionEntry.CreateInventorySlotData.
    // The native value-type layout is 0x10 bytes earlier than the boxed metadata
    // offsets previously observed in IL2CPP metadata:
    //   InventoryBuffer.Amount           metadata 0x20 -> native +0x10
    //   InventoryBuffer.MaxAmountOverride metadata 0x24 -> native +0x14
    //   Data.Stacks                      metadata 0x44 -> native +0x34
    //   Data.MaxStacks                   metadata 0x48 -> native +0x38
    //
    // This prologue is unique in the supplied client/server GameAssembly builds and
    // lets the diagnostic build prove whether the live ECS amount is already capped
    // before the UI receives it.
    private static readonly byte[] CreateInventorySlotDataPattern =
    {
        0x44, 0x89, 0x44, 0x24, 0x18,
        0x48, 0x89, 0x54, 0x24, 0x10,
        0x55, 0x56, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
        0x48, 0x8D, 0xAC, 0x24, 0xD8, 0xFB, 0xFF, 0xFF,
        0x48, 0x81, 0xEC, 0x28, 0x05, 0x00, 0x00
    };

    private static readonly bool[] CreateInventorySlotDataPatternMask =
    {
        true, true, true, true, true,
        true, true, true, true, true,
        true, true, true, true, true, true, true, true, true, true,
        true, true, true, true, true, true, true, true,
        true, true, true, true, true, true, true
    };

    private const int InventoryBufferAmountNativeOffset = 0x10;
    private const int InventoryBufferMaxAmountOverrideNativeOffset = 0x14;
    private const int InventorySlotDataStacksNativeOffset = 0x34;
    private const int InventorySlotDataMaxStacksNativeOffset = 0x38;
    private const int DiagnosticTraceLimitPerStage = 40;

    private ConfigFile? _vStackConfig;
    private ConfigEntry<float>? _multiplier;
    private INativeDetour? _detour;
    private SettingsClampHalfDelegate? _original;
    private SettingsClampHalfDelegate? _detourDelegate;
    private bool _loggedFirstIntercept;
    private bool _loggedInvalidConfig;
    private readonly List<MemoryPatch> _inventoryWirePatches = new();

    // Wire/UI diagnostic detours. These are intentionally read-only: they only log
    // the values flowing through the already identified inventory paths.
    private IntPtr _inventoryWriterTarget;
    private IntPtr _inventoryReaderTarget;
    private INativeDetour? _boundedWriterDetour;
    private INativeDetour? _boundedReaderDetour;
    private INativeDetour? _createInventorySlotDataDetour;
    private BoundedIntWriterDelegate? _boundedWriterOriginal;
    private BoundedIntReaderDelegate? _boundedReaderOriginal;
    private CreateInventorySlotDataDelegate? _createInventorySlotDataOriginal;
    private BoundedIntWriterDelegate? _boundedWriterDelegate;
    private BoundedIntReaderDelegate? _boundedReaderDelegate;
    private CreateInventorySlotDataDelegate? _createInventorySlotDataDelegate;
    private int _wireWriteTraceCount;
    private int _wireReadTraceCount;
    private int _uiTraceCount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort SettingsClampHalfDelegate(float value, float min, float max, IntPtr fieldName);

    // Generated bounded integer writer:
    //   writer, min, max, value, MethodInfo* -> bits written
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BoundedIntWriterDelegate(IntPtr writer, int min, int max, int value, IntPtr methodInfo);

    // Generated bounded integer reader:
    //   reader, min, max, MethodInfo* -> reconstructed value
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BoundedIntReaderDelegate(IntPtr reader, int min, int max, IntPtr methodInfo);

    // Large value-type return uses the hidden result buffer as RCX. The second
    // argument (RDX) is the native InventoryBuffer element pointer.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInventorySlotDataDelegate(
        IntPtr resultBuffer,
        IntPtr inventoryBuffer,
        int arg3,
        IntPtr arg4);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public override void Load()
    {
        // Keep the user-facing file simple and predictable:
        // BepInEx/config/VStack.cfg
        _vStackConfig = new ConfigFile(Path.Combine(Paths.ConfigPath, "VStack.cfg"), true);
        _multiplier = _vStackConfig.Bind(
            "Stacks",
            "Multiplier",
            DefaultMultiplier,
            "Inventory stack multiplier. Default: 1000. Examples: 10, 100, 500, 1000, 5000. " +
            "Valid positive range: 0.01 to 65504. Restart the game/server after editing this text file.");

        // Install the existing, proven stack-setting detour first. This path is kept
        // independent from the visual-count wire patch so a future game update can
        // fail the latter safely without changing unrelated settings.
        try
        {
            IntPtr target = FindSettingsClampHalf();
            if (target == IntPtr.Zero)
            {
                Log.LogError("SettingsClamp::Half signature was not found. The game may have updated; stack multiplier hook was not applied.");
            }
            else
            {
                SettingsClampHalfDelegate detourDelegate = SettingsClampHalfDetour;
                _detourDelegate = detourDelegate;
                _detour = INativeDetour.CreateAndApply<SettingsClampHalfDelegate>(target, detourDelegate, out var original);
                _original = original;

                Log.LogInfo($"{PluginName} {PluginVersion} loaded (managed-only BepInEx hook).");
                Log.LogInfo($"Hooked SettingsClamp::Half at 0x{target.ToInt64():X}.");
                Log.LogInfo($"Target setting: {TargetSettingName}; configured multiplier: x{GetMultiplier():0.###}.");
                Log.LogInfo($"Config file: {_vStackConfig.ConfigFilePath}");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to install SettingsClamp::Half detour: {ex}");
        }

        // Fix the 4095/4096 visible-count limitation at its actual source: the
        // generated InventoryBuffer snapshot wire bounds. This remains managed-only;
        // no custom native DLL or external hooking library is used.
        bool wirePatchInstalled = false;
        try
        {
            InstallInventoryWirePatch();
            wirePatchInstalled = true;
            Log.LogInfo(
                $"InventoryBuffer extended-count wire patch enabled (0..{ExtendedInventoryWireMaximum}). " +
                "VStack must be installed on both the server and every connecting client.");
        }
        catch (Exception ex)
        {
            try
            {
                RestoreInventoryWirePatch();
            }
            catch (Exception restoreEx)
            {
                Log.LogWarning($"Additional error while rolling back InventoryBuffer wire patch: {restoreEx}");
            }

            Log.LogError(
                "Failed to install the InventoryBuffer extended-count wire patch. " +
                "The stack multiplier hook can still work, but inventory counts above 4095 may remain visually capped. " +
                $"Details: {ex}");
        }

        if (wirePatchInstalled)
        {
            try
            {
                InstallInventoryDiagnostics();
                Log.LogWarning(
                    "VStack inventory-count diagnostic tracing is ENABLED for this test build. " +
                    "Reproduce one >4095 stack, then send the new BepInEx log.");
            }
            catch (Exception ex)
            {
                try
                {
                    RemoveInventoryDiagnostics();
                }
                catch (Exception cleanupEx)
                {
                    Log.LogWarning($"Additional error while removing partial inventory diagnostics: {cleanupEx}");
                }

                Log.LogError(
                    "Inventory wire patch is active, but diagnostic tracing could not be installed. " +
                    $"Details: {ex}");
            }
        }
    }

    public override bool Unload()
    {
        try
        {
            RemoveInventoryDiagnostics();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Error while removing inventory diagnostic detours: {ex}");
        }

        try
        {
            RestoreInventoryWirePatch();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Error while restoring InventoryBuffer wire patch: {ex}");
        }

        try
        {
            _detour?.Dispose();
            _detour = null;
            _original = null;
            _detourDelegate = null;
            _multiplier = null;
            _vStackConfig = null;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Error while removing native detour: {ex}");
        }

        return true;
    }

    private ushort SettingsClampHalfDetour(float value, float min, float max, IntPtr fieldName)
    {
        SettingsClampHalfDelegate? original = _original;
        if (original is null)
            return 0;

        if (IsIl2CppStringEqual(fieldName, TargetSettingName))
        {
            float multiplier = GetMultiplier();

            // Replace only InventoryStacksModifier. Other settings pass through untouched.
            value = multiplier;
            min = 0.0f;
            max = multiplier;

            if (!_loggedFirstIntercept)
            {
                _loggedFirstIntercept = true;
                Log.LogInfo($"{TargetSettingName} intercepted and forced to x{multiplier:0.###}.");
            }
        }

        return original(value, min, max, fieldName);
    }

    private float GetMultiplier()
    {
        float configured = _multiplier?.Value ?? DefaultMultiplier;

        if (float.IsNaN(configured) || float.IsInfinity(configured) || configured <= 0.0f)
        {
            if (!_loggedInvalidConfig)
            {
                _loggedInvalidConfig = true;
                Log.LogWarning($"Invalid Multiplier '{configured}'. Using default x{DefaultMultiplier:0.###}.");
            }

            return DefaultMultiplier;
        }

        if (configured > MaximumMultiplier)
        {
            if (!_loggedInvalidConfig)
            {
                _loggedInvalidConfig = true;
                Log.LogWarning($"Multiplier x{configured:0.###} exceeds the half-precision limit. Using x{MaximumMultiplier:0.###}.");
            }

            return MaximumMultiplier;
        }

        return Math.Max(configured, MinimumMultiplier);
    }

    private static unsafe bool IsIl2CppStringEqual(IntPtr stringObject, string expected)
    {
        if (stringObject == IntPtr.Zero)
            return false;

        // 64-bit IL2CPP string layout:
        // 0x00 object header (16 bytes)
        // 0x10 int32 length
        // 0x14 UTF-16 character data
        byte* raw = (byte*)stringObject;
        int length = *(int*)(raw + 0x10);
        if (length != expected.Length || length < 0 || length > 128)
            return false;

        char* chars = (char*)(raw + 0x14);
        for (int i = 0; i < length; i++)
        {
            if (chars[i] != expected[i])
                return false;
        }

        return true;
    }

    private unsafe void InstallInventoryWirePatch()
    {
        if (_inventoryWirePatches.Count != 0)
            return;

        List<IntPtr> serializerAnchors = FindExecutablePatternMatches(
            InventorySerializerPattern,
            InventorySerializerPatternMask);

        if (serializerAnchors.Count != ExpectedInventorySerializerImplementations)
        {
            throw new InvalidOperationException(
                $"Expected exactly {ExpectedInventorySerializerImplementations} InventoryBuffer serializer implementations, " +
                $"found {serializerAnchors.Count}. Refusing to patch.");
        }

        serializerAnchors.Sort((left, right) => left.ToInt64().CompareTo(right.ToInt64()));
        var candidates = new List<MemoryPatch>(ExpectedInventorySerializerImplementations * 3);
        var writerTargets = new HashSet<IntPtr>();
        var readerTargets = new HashSet<IntPtr>();

        for (int implementationIndex = 0; implementationIndex < serializerAnchors.Count; implementationIndex++)
        {
            IntPtr serializerAnchor = serializerAnchors[implementationIndex];
            int displayIndex = implementationIndex + 1;

            IntPtr serializerMaximum = IntPtr.Add(serializerAnchor, SerializerMaximumImmediateOffset);
            IntPtr writerCall = IntPtr.Add(serializerAnchor, SerializerWriterCallOffset);
            writerTargets.Add(ResolveRelativeCallTarget(writerCall, $"InventoryBuffer serializer #{displayIndex} bounded writer"));

            IntPtr deserializerSearchStart = IntPtr.Add(serializerAnchor, DeserializerSearchStartOffset);
            List<IntPtr> deserializerSites = FindPatternMatchesInRange(
                deserializerSearchStart,
                DeserializerSearchLength,
                InventoryDeserializerBoundPattern,
                InventoryDeserializerBoundPatternMask);

            if (deserializerSites.Count != 2)
            {
                throw new InvalidOperationException(
                    $"InventoryBuffer implementation #{displayIndex}: expected exactly 2 deserializer bound sites, " +
                    $"found {deserializerSites.Count}. Refusing to patch.");
            }

            deserializerSites.Sort((left, right) => left.ToInt64().CompareTo(right.ToInt64()));

            foreach (IntPtr deserializerSite in deserializerSites)
            {
                IntPtr readerCall = IntPtr.Add(deserializerSite, DeserializerReaderCallOffset);
                readerTargets.Add(ResolveRelativeCallTarget(readerCall, $"InventoryBuffer deserializer #{displayIndex} bounded reader"));
            }

            candidates.Add(new MemoryPatch(
                serializerMaximum,
                VanillaInventoryWireMaximum,
                ExtendedInventoryWireMaximum,
                $"serializer #{displayIndex} shared Amount/MaxAmountOverride bound"));

            candidates.Add(new MemoryPatch(
                IntPtr.Add(deserializerSites[0], DeserializerMaximumImmediateOffset),
                VanillaInventoryWireMaximum,
                ExtendedInventoryWireMaximum,
                $"deserializer #{displayIndex} Amount bound"));

            candidates.Add(new MemoryPatch(
                IntPtr.Add(deserializerSites[1], DeserializerMaximumImmediateOffset),
                VanillaInventoryWireMaximum,
                ExtendedInventoryWireMaximum,
                $"deserializer #{displayIndex} MaxAmountOverride bound"));
        }

        var uniqueAddresses = new HashSet<IntPtr>();
        foreach (MemoryPatch patch in candidates)
        {
            if (!uniqueAddresses.Add(patch.Address))
            {
                throw new InvalidOperationException(
                    $"Duplicate InventoryBuffer patch address 0x{patch.Address.ToInt64():X} detected. Refusing to patch.");
            }

            int current = Marshal.ReadInt32(patch.Address);
            if (current != patch.OriginalValue)
            {
                throw new InvalidOperationException(
                    $"Unexpected value 0x{current:X8} at 0x{patch.Address.ToInt64():X} for {patch.Description}; " +
                    $"expected 0x{patch.OriginalValue:X8}. Refusing to patch.");
            }
        }

        if (writerTargets.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected all InventoryBuffer serializers to share one bounded writer target; found {writerTargets.Count}. Refusing diagnostics.");
        }

        if (readerTargets.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected all InventoryBuffer deserializers to share one bounded reader target; found {readerTargets.Count}. Refusing diagnostics.");
        }

        foreach (IntPtr target in writerTargets)
            _inventoryWriterTarget = target;
        foreach (IntPtr target in readerTargets)
            _inventoryReaderTarget = target;

        // Record all validated targets before the first write so recovery can restore
        // even a write that succeeds but then fails during cache/protection handling.
        _inventoryWirePatches.AddRange(candidates);

        try
        {
            foreach (MemoryPatch patch in candidates)
            {
                WriteProtectedInt32(patch.Address, patch.ReplacementValue);
                Log.LogInfo(
                    $"Patched InventoryBuffer {patch.Description} at 0x{patch.Address.ToInt64():X}: " +
                    $"{patch.OriginalValue} -> {patch.ReplacementValue}.");
            }

            Log.LogInfo(
                $"InventoryBuffer wire patch verified across {serializerAnchors.Count} generated serializer implementations " +
                $"({candidates.Count} bounds total).");
        }
        catch
        {
            RestoreInventoryWirePatch();
            throw;
        }
    }

    private void InstallInventoryDiagnostics()
    {
        RemoveInventoryDiagnostics();
        _wireWriteTraceCount = 0;
        _wireReadTraceCount = 0;
        _uiTraceCount = 0;

        if (_inventoryWriterTarget == IntPtr.Zero || _inventoryReaderTarget == IntPtr.Zero)
            throw new InvalidOperationException("Inventory wire diagnostic targets were not resolved.");

        BoundedIntWriterDelegate writerDelegate = BoundedIntWriterDetour;
        _boundedWriterDelegate = writerDelegate;
        _boundedWriterDetour = INativeDetour.CreateAndApply<BoundedIntWriterDelegate>(
            _inventoryWriterTarget,
            writerDelegate,
            out var writerOriginal);
        _boundedWriterOriginal = writerOriginal;
        Log.LogInfo($"Diagnostic hook: bounded InventoryBuffer writer at 0x{_inventoryWriterTarget.ToInt64():X}.");

        BoundedIntReaderDelegate readerDelegate = BoundedIntReaderDetour;
        _boundedReaderDelegate = readerDelegate;
        _boundedReaderDetour = INativeDetour.CreateAndApply<BoundedIntReaderDelegate>(
            _inventoryReaderTarget,
            readerDelegate,
            out var readerOriginal);
        _boundedReaderOriginal = readerOriginal;
        Log.LogInfo($"Diagnostic hook: bounded InventoryBuffer reader at 0x{_inventoryReaderTarget.ToInt64():X}.");

        List<IntPtr> uiMatches = FindExecutablePatternMatches(
            CreateInventorySlotDataPattern,
            CreateInventorySlotDataPatternMask);

        if (uiMatches.Count == 1)
        {
            IntPtr uiTarget = uiMatches[0];
            CreateInventorySlotDataDelegate uiDelegate = CreateInventorySlotDataDetour;
            _createInventorySlotDataDelegate = uiDelegate;
            _createInventorySlotDataDetour = INativeDetour.CreateAndApply<CreateInventorySlotDataDelegate>(
                uiTarget,
                uiDelegate,
                out var uiOriginal);
            _createInventorySlotDataOriginal = uiOriginal;
            Log.LogInfo($"Diagnostic hook: ItemGridSelectionEntry.CreateInventorySlotData at 0x{uiTarget.ToInt64():X}.");
        }
        else
        {
            Log.LogWarning(
                $"Diagnostic UI hook not installed: CreateInventorySlotData signature had {uiMatches.Count} matches. " +
                "Wire tracing remains active.");
        }
    }

    private void RemoveInventoryDiagnostics()
    {
        _createInventorySlotDataDetour?.Dispose();
        _createInventorySlotDataDetour = null;
        _createInventorySlotDataOriginal = null;
        _createInventorySlotDataDelegate = null;

        _boundedReaderDetour?.Dispose();
        _boundedReaderDetour = null;
        _boundedReaderOriginal = null;
        _boundedReaderDelegate = null;

        _boundedWriterDetour?.Dispose();
        _boundedWriterDetour = null;
        _boundedWriterOriginal = null;
        _boundedWriterDelegate = null;
    }

    private int BoundedIntWriterDetour(IntPtr writer, int min, int max, int value, IntPtr methodInfo)
    {
        BoundedIntWriterDelegate? original = _boundedWriterOriginal;
        if (original is null)
            return 0;

        int result = original(writer, min, max, value, methodInfo);

        // The inventory calls patched by VStack are now min=0/max=Int32.MaxValue.
        // Log the threshold itself as well as values above it so we can distinguish
        // "already capped before serialization" from "true extended value reached the writer".
        if (min == 0 && max == ExtendedInventoryWireMaximum && value >= VanillaInventoryWireMaximum)
        {
            int traceNumber = Interlocked.Increment(ref _wireWriteTraceCount);
            if (traceNumber <= DiagnosticTraceLimitPerStage)
            {
                Log.LogWarning(
                    $"VSTACK-DIAG WIRE-WRITE #{traceNumber}: value={value}, min={min}, max={max}, bits={result}.");
            }
        }

        return result;
    }

    private int BoundedIntReaderDetour(IntPtr reader, int min, int max, IntPtr methodInfo)
    {
        BoundedIntReaderDelegate? original = _boundedReaderOriginal;
        if (original is null)
            return 0;

        int value = original(reader, min, max, methodInfo);

        if (min == 0 && max == ExtendedInventoryWireMaximum && value >= VanillaInventoryWireMaximum)
        {
            int traceNumber = Interlocked.Increment(ref _wireReadTraceCount);
            if (traceNumber <= DiagnosticTraceLimitPerStage)
            {
                Log.LogWarning(
                    $"VSTACK-DIAG WIRE-READ #{traceNumber}: value={value}, min={min}, max={max}.");
            }
        }

        return value;
    }

    private IntPtr CreateInventorySlotDataDetour(
        IntPtr resultBuffer,
        IntPtr inventoryBuffer,
        int arg3,
        IntPtr arg4)
    {
        CreateInventorySlotDataDelegate? original = _createInventorySlotDataOriginal;
        if (original is null)
            return resultBuffer;

        IntPtr returned = original(resultBuffer, inventoryBuffer, arg3, arg4);
        IntPtr data = returned != IntPtr.Zero ? returned : resultBuffer;

        if (inventoryBuffer == IntPtr.Zero || data == IntPtr.Zero)
            return returned;

        int amount = Marshal.ReadInt32(inventoryBuffer, InventoryBufferAmountNativeOffset);
        int maxAmountOverride = Marshal.ReadInt32(inventoryBuffer, InventoryBufferMaxAmountOverrideNativeOffset);
        int stacks = Marshal.ReadInt32(data, InventorySlotDataStacksNativeOffset);
        int maxStacks = Marshal.ReadInt32(data, InventorySlotDataMaxStacksNativeOffset);

        if (amount >= VanillaInventoryWireMaximum || stacks >= VanillaInventoryWireMaximum)
        {
            int traceNumber = Interlocked.Increment(ref _uiTraceCount);
            if (traceNumber <= DiagnosticTraceLimitPerStage)
            {
                Log.LogWarning(
                    $"VSTACK-DIAG UI #{traceNumber}: InventoryBuffer.Amount={amount}, " +
                    $"MaxAmountOverride={maxAmountOverride}, Data.Stacks={stacks}, Data.MaxStacks={maxStacks}, arg3={arg3}.");
            }
        }

        return returned;
    }

    private static IntPtr ResolveRelativeCallTarget(IntPtr callAddress, string description)
    {
        if (Marshal.ReadByte(callAddress) != 0xE8)
        {
            throw new InvalidOperationException(
                $"Expected CALL rel32 for {description} at 0x{callAddress.ToInt64():X}. Refusing to continue.");
        }

        int displacement = Marshal.ReadInt32(callAddress, 1);
        return new IntPtr(callAddress.ToInt64() + 5L + displacement);
    }

    private void RestoreInventoryWirePatch()
    {
        if (_inventoryWirePatches.Count == 0)
            return;

        Exception? firstError = null;

        for (int i = _inventoryWirePatches.Count - 1; i >= 0; i--)
        {
            MemoryPatch patch = _inventoryWirePatches[i];
            try
            {
                int current = Marshal.ReadInt32(patch.Address);
                if (current == patch.ReplacementValue)
                {
                    WriteProtectedInt32(patch.Address, patch.OriginalValue);
                    Log.LogInfo($"Restored InventoryBuffer {patch.Description} at 0x{patch.Address.ToInt64():X}.");
                }
                else if (current != patch.OriginalValue)
                {
                    Log.LogWarning(
                        $"Not restoring {patch.Description} at 0x{patch.Address.ToInt64():X}; " +
                        $"current value 0x{current:X8} is neither VStack's replacement nor the original value.");
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        if (firstError is null)
            _inventoryWirePatches.Clear();

        if (firstError is not null)
            throw firstError;
    }

    private static void WriteProtectedInt32(IntPtr address, int value)
    {
        UIntPtr size = new UIntPtr(4u);
        if (!VirtualProtect(address, size, PageExecuteReadWrite, out uint oldProtect))
        {
            throw new InvalidOperationException(
                $"VirtualProtect(RWX) failed at 0x{address.ToInt64():X}; Win32 error {Marshal.GetLastWin32Error()}.");
        }

        Exception? writeError = null;
        try
        {
            Marshal.WriteInt32(address, value);

            if (Marshal.ReadInt32(address) != value)
            {
                throw new InvalidOperationException($"Memory verification failed at 0x{address.ToInt64():X}.");
            }

            if (!FlushInstructionCache(GetCurrentProcess(), address, size))
            {
                throw new InvalidOperationException(
                    $"FlushInstructionCache failed at 0x{address.ToInt64():X}; Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }
        catch (Exception ex)
        {
            writeError = ex;
        }
        finally
        {
            if (!VirtualProtect(address, size, oldProtect, out _))
            {
                Exception protectionError = new InvalidOperationException(
                    $"VirtualProtect(restore) failed at 0x{address.ToInt64():X}; Win32 error {Marshal.GetLastWin32Error()}.");
                writeError ??= protectionError;
            }
        }

        if (writeError is not null)
            throw writeError;
    }

    private unsafe List<IntPtr> FindExecutablePatternMatches(byte[] pattern, bool[] mask)
    {
        if (pattern.Length == 0 || pattern.Length != mask.Length)
            throw new ArgumentException("Pattern and mask must be non-empty and the same length.");

        ProcessModule gameAssembly = GetGameAssemblyModule();
        byte* imageBase = (byte*)gameAssembly.BaseAddress;
        ushort numberOfSections;
        byte* section;
        GetPeSectionTable(imageBase, out numberOfSections, out section);

        var matches = new List<IntPtr>();

        for (int i = 0; i < numberOfSections; i++, section += 40)
        {
            uint virtualSize = *(uint*)(section + 0x08);
            uint virtualAddress = *(uint*)(section + 0x0C);
            uint characteristics = *(uint*)(section + 0x24);

            if ((characteristics & ImageScnMemExecute) == 0 || virtualSize < pattern.Length)
                continue;

            byte* start = imageBase + virtualAddress;
            int length = checked((int)virtualSize);
            for (int offset = 0; offset <= length - pattern.Length; offset++)
            {
                if (MatchesPattern(start + offset, pattern, mask))
                    matches.Add((IntPtr)(start + offset));
            }
        }

        return matches;
    }

    private static unsafe List<IntPtr> FindPatternMatchesInRange(
        IntPtr rangeStart,
        int rangeLength,
        byte[] pattern,
        bool[] mask)
    {
        if (rangeStart == IntPtr.Zero)
            throw new ArgumentNullException(nameof(rangeStart));
        if (rangeLength < pattern.Length)
            throw new ArgumentOutOfRangeException(nameof(rangeLength));
        if (pattern.Length == 0 || pattern.Length != mask.Length)
            throw new ArgumentException("Pattern and mask must be non-empty and the same length.");

        var matches = new List<IntPtr>();
        byte* start = (byte*)rangeStart;

        for (int offset = 0; offset <= rangeLength - pattern.Length; offset++)
        {
            if (MatchesPattern(start + offset, pattern, mask))
                matches.Add((IntPtr)(start + offset));
        }

        return matches;
    }

    private static unsafe bool MatchesPattern(byte* address, byte[] pattern, bool[] mask)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (mask[i] && address[i] != pattern[i])
                return false;
        }

        return true;
    }

    private static ProcessModule GetGameAssemblyModule()
    {
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                return module;
        }

        throw new InvalidOperationException("GameAssembly.dll is not loaded.");
    }

    private static unsafe void GetPeSectionTable(byte* imageBase, out ushort numberOfSections, out byte* sectionTable)
    {
        if (*(ushort*)imageBase != 0x5A4D) // MZ
            throw new InvalidOperationException("GameAssembly.dll has an invalid DOS header.");

        int peOffset = *(int*)(imageBase + 0x3C);
        byte* ntHeaders = imageBase + peOffset;
        if (*(uint*)ntHeaders != 0x00004550) // PE\0\0
            throw new InvalidOperationException("GameAssembly.dll has an invalid PE header.");

        numberOfSections = *(ushort*)(ntHeaders + 0x06);
        ushort optionalHeaderSize = *(ushort*)(ntHeaders + 0x14);
        sectionTable = ntHeaders + 0x18 + optionalHeaderSize;
    }

    private unsafe IntPtr FindSettingsClampHalf()
    {
        ProcessModule? gameAssembly = null;
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
            {
                gameAssembly = module;
                break;
            }
        }

        if (gameAssembly is null)
            throw new InvalidOperationException("GameAssembly.dll is not loaded.");

        byte* imageBase = (byte*)gameAssembly.BaseAddress;
        if (*(ushort*)imageBase != 0x5A4D) // MZ
            throw new InvalidOperationException("GameAssembly.dll has an invalid DOS header.");

        int peOffset = *(int*)(imageBase + 0x3C);
        byte* ntHeaders = imageBase + peOffset;
        if (*(uint*)ntHeaders != 0x00004550) // PE\0\0
            throw new InvalidOperationException("GameAssembly.dll has an invalid PE header.");

        ushort numberOfSections = *(ushort*)(ntHeaders + 0x06);
        ushort optionalHeaderSize = *(ushort*)(ntHeaders + 0x14);
        byte* section = ntHeaders + 0x18 + optionalHeaderSize;

        IntPtr found = IntPtr.Zero;
        int matches = 0;

        for (int i = 0; i < numberOfSections; i++, section += 40)
        {
            uint virtualSize = *(uint*)(section + 0x08);
            uint virtualAddress = *(uint*)(section + 0x0C);
            uint characteristics = *(uint*)(section + 0x24);

            if ((characteristics & ImageScnMemExecute) == 0 || virtualSize < Pattern.Length)
                continue;

            byte* start = imageBase + virtualAddress;
            int length = checked((int)virtualSize);
            for (int offset = 0; offset <= length - Pattern.Length; offset++)
            {
                if (!Matches(start + offset))
                    continue;

                matches++;
                found = (IntPtr)(start + offset);
            }
        }

        if (matches == 0)
            return IntPtr.Zero;

        if (matches != 1)
            throw new InvalidOperationException($"SettingsClamp::Half signature was not unique ({matches} matches). Refusing to hook.");

        return found;
    }

    private static unsafe bool Matches(byte* address)
    {
        for (int i = 0; i < Pattern.Length; i++)
        {
            if (PatternMask[i] && address[i] != Pattern[i])
                return false;
        }

        return true;
    }

    private sealed class MemoryPatch
    {
        public MemoryPatch(IntPtr address, int originalValue, int replacementValue, string description)
        {
            Address = address;
            OriginalValue = originalValue;
            ReplacementValue = replacementValue;
            Description = description;
        }

        public IntPtr Address { get; }
        public int OriginalValue { get; }
        public int ReplacementValue { get; }
        public string Description { get; }
    }
}
