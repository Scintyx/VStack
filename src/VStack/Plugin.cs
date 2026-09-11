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

    // Instruction sequences inside the generated InventoryBuffer serializers.
    // Current supplied V Rising client/server builds contain THREE generated
    // implementations of the same 24-byte InventoryBuffer payload. Two share one
    // register-allocation shape (variant A), while the third uses a different
    // register-allocation shape (variant B). All three carry the same 0x0FFF bounds
    // for Amount and MaxAmountOverride and must be patched.
    //
    // Variant A loads the shared 0x0FFF immediate into r13d.
    private static readonly byte[] InventorySerializerPatternA =
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

    private static readonly bool[] InventorySerializerPatternAMask =
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

    // Variant B is the third generated implementation. It has the same 24-byte
    // InventoryBuffer element layout and the same two 4095-bounded fields, but uses
    // a different register allocation (r12d instead of r13d).
    private static readonly byte[] InventorySerializerPatternB =
    {
        0x44, 0x8B, 0x7C, 0x24, 0x30,
        0x41, 0xBC, 0xFF, 0x0F, 0x00, 0x00,
        0x8B, 0x45, 0x04,
        0x41, 0x39, 0x06,
        0x0F, 0x8E,
        0x00, 0x00, 0x00, 0x00,
        0x8B, 0x06,
        0x41, 0x3B, 0xC4,
        0x0F, 0x10, 0x06,
        0x41, 0x0F, 0x4F, 0xC4,
        0xF2, 0x0F, 0x10, 0x4E, 0x10
    };

    private static readonly bool[] InventorySerializerPatternBMask =
    {
        true, true, true, true, true,
        true, true, true, true, true, true,
        true, true, true,
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

    // ItemGridSelectionEntry.RefreshData extracts Data.Stacks and calls this
    // amount-formatting helper before assigning the inventory label text. The call
    // site is a much safer/native-simple place to observe and override the displayed
    // stack text than detouring the large CreateInventorySlotData value-type method.
    //
    //   psrldq xmm6, 4
    //   xor    r8d, r8d
    //   movd   ecx, xmm6      ; Data.Stacks
    //   mov    dl, 1
    //   call   <amount formatter>
    //   mov    rcx, [rsi+A0h]
    private static readonly byte[] InventoryAmountFormatterCallSitePattern =
    {
        0x66, 0x0F, 0x73, 0xDE, 0x04,
        0x45, 0x33, 0xC0,
        0x66, 0x0F, 0x7E, 0xF1,
        0xB2, 0x01,
        0xE8,
        0x00, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0x8E, 0xA0, 0x00, 0x00, 0x00
    };

    private static readonly bool[] InventoryAmountFormatterCallSitePatternMask =
    {
        true, true, true, true, true,
        true, true, true,
        true, true, true, true,
        true, true,
        true,
        false, false, false, false,
        true, true, true, true, true, true, true
    };

    private const int InventoryAmountFormatterCallInstructionOffset = 14;

    // Active V Rising inventory replication is Burst-compiled. The IL2CPP-generated
    // GameAssembly serializer copies are fallback/dead paths in the tested build.
    //
    // The live Burst server serializers encode InventoryBuffer.Amount and
    // MaxAmountOverride as two inlined 12-bit values. The matching Burst client
    // deserializers read those same two fields into a 24-byte InventoryBuffer
    // element. VStack widens only these validated inventory sites to 31 bits.
    private const int VanillaInventoryBitWidth = 12;
    private const int ExtendedInventoryBitWidth = 31;

    // Server Burst clamp shape #1:
    //   cmp ecx, 0x0FFF
    //   mov eax, 0x0FFF
    //   cmovge ecx, eax
    //   ...
    private static readonly byte[] BurstServerClampPatternA =
    {
        0x81, 0xF9, 0xFF, 0x0F, 0x00, 0x00,
        0xB8, 0xFF, 0x0F, 0x00, 0x00,
        0x0F, 0x4D, 0xC8,
        0x85, 0xC9,
        0xB8, 0x00, 0x00, 0x00, 0x00,
        0x0F, 0x4E, 0xC8
    };

    // Server Burst clamp shape #2:
    //   cmp r15d, 0x0FFF
    //   mov eax, 0x0FFF
    //   cmovge r15d, eax
    //   ...
    private static readonly byte[] BurstServerClampPatternB =
    {
        0x41, 0x81, 0xFF, 0xFF, 0x0F, 0x00, 0x00,
        0xB8, 0xFF, 0x0F, 0x00, 0x00,
        0x44, 0x0F, 0x4D, 0xF8,
        0x45, 0x85, 0xFF,
        0xB8, 0x00, 0x00, 0x00, 0x00,
        0x44, 0x0F, 0x4E, 0xF8
    };

    private static readonly bool[] BurstServerClampPatternAMask = CreateAllTrueMask(BurstServerClampPatternA.Length);
    private static readonly bool[] BurstServerClampPatternBMask = CreateAllTrueMask(BurstServerClampPatternB.Length);

    // Client Burst first inventory field:
    //   mov rcx, [rdi]
    //   mov edx, [rdi+8]
    //   sub rsp, 20h
    //   mov r8d, 0Ch
    //   call <bit reader>
    //   add [rdi+14h], 0Ch
    private static readonly byte[] BurstClientFirstReadPattern =
    {
        0x48, 0x8B, 0x0F,
        0x8B, 0x57, 0x08,
        0x48, 0x83, 0xEC, 0x20,
        0x41, 0xB8, 0x0C, 0x00, 0x00, 0x00,
        0xE8, 0x00, 0x00, 0x00, 0x00,
        0x48, 0x83, 0xC4, 0x20,
        0x83, 0x47, 0x14, 0x0C
    };

    private static readonly bool[] BurstClientFirstReadPatternMask =
    {
        true, true, true,
        true, true, true,
        true, true, true, true,
        true, true, true, true, true, true,
        true, false, false, false, false,
        true, true, true, true,
        true, true, true, true
    };

    // Client Burst second inventory field:
    //   sub rsp, 20h
    //   mov r8d, 0Ch
    //   call <bit reader>
    //   ...
    //   add r9d, 0Ch
    //
    // This signature is inventory-specific in the supplied current Burst DLLs:
    // exactly six matches (3 ghost layouts x 2 Burst compilation sets).
    private static readonly byte[] BurstClientSecondReadPattern =
    {
        0x48, 0x83, 0xEC, 0x20,
        0x41, 0xB8, 0x0C, 0x00, 0x00, 0x00,
        0xE8, 0x00, 0x00, 0x00, 0x00,
        0x48, 0x83, 0xC4, 0x20,
        0x44, 0x8B, 0x4F, 0x14,
        0x41, 0x83, 0xC1, 0x0C,
        0x44, 0x89, 0x4F, 0x14
    };

    private static readonly bool[] BurstClientSecondReadPatternMask =
    {
        true, true, true, true,
        true, true, true, true, true, true,
        true, false, false, false, false,
        true, true, true, true,
        true, true, true, true,
        true, true, true, true,
        true, true, true, true
    };

    private static readonly byte[] BurstBitWidthMovEdx12 = { 0xBA, 0x0C, 0x00, 0x00, 0x00 };
    private static readonly bool[] BurstBitWidthMovEdx12Mask = CreateAllTrueMask(BurstBitWidthMovEdx12.Length);

    private const int ExpectedBurstServerClampMatchesPerShape = 6;
    private const int ExpectedBurstClientDecoderPairs = 6;
    private const int BurstClientFirstReadWidthImmediateOffset = 12;
    private const int BurstClientFirstReadBitPositionImmediateOffset = 28;
    private const int BurstClientSecondReadWidthImmediateOffset = 6;
    private const int BurstClientSecondReadBitPositionImmediateOffset = 26;
    private const int BurstClientFirstReadSearchBack = 0x200;

    private const int ExpectedInventorySerializerVariantACount = 2;
    private const int ExpectedInventorySerializerVariantBCount = 1;
    private const int ExpectedInventorySerializerImplementations =
        ExpectedInventorySerializerVariantACount + ExpectedInventorySerializerVariantBCount;

    private const int SerializerMaximumImmediateOffset = 7;
    private const int SerializerWriterCallOffsetVariantA = 0x4F;
    private const int SerializerWriterCallOffsetVariantB = 0x53;
    private const int DeserializerMaximumImmediateOffset = 11;
    private const int DeserializerReaderCallOffset = 18;
    private const int DiagnosticTraceLimitPerStage = 80;

    // The three generated implementations place their matching deserializers at
    // different relative distances. This local window covers all three while still
    // requiring exactly two matching 0x0FFF bounds per implementation.
    private const int DeserializerSearchStartOffset = 0x400;
    private const int DeserializerSearchLength = 0x1000;

    private ConfigFile? _vStackConfig;
    private ConfigEntry<float>? _multiplier;
    private INativeDetour? _detour;
    private SettingsClampHalfDelegate? _original;
    private SettingsClampHalfDelegate? _detourDelegate;
    private bool _loggedFirstIntercept;
    private bool _loggedInvalidConfig;
    private readonly List<MemoryPatch> _inventoryWirePatches = new();
    private INativeDetour? _amountFormatterDetour;
    private InventoryAmountFormatterDelegate? _amountFormatterOriginal;
    private InventoryAmountFormatterDelegate? _amountFormatterDelegate;
    private bool _loggedFormatterAtVanillaCap;
    private bool _loggedFormatterOverride;
    private bool _loggedFormatterFailure;

    private readonly List<BurstMemoryPatch> _burstWirePatches = new();
    private BurstPatchRole _burstPatchRole = BurstPatchRole.None;

    // Temporary v6 diagnostics. These hook only the small shared bounded-int
    // reader/writer helpers already used by the generated InventoryBuffer wire path.
    // No ItemGridSelectionEntry/CreateInventorySlotData detour is installed.
    private IntPtr _inventoryWriterTarget;
    private IntPtr _inventoryReaderTarget;
    private INativeDetour? _boundedWriterDetour;
    private INativeDetour? _boundedReaderDetour;
    private BoundedIntWriterDelegate? _boundedWriterOriginal;
    private BoundedIntReaderDelegate? _boundedReaderOriginal;
    private BoundedIntWriterDelegate? _boundedWriterDelegate;
    private BoundedIntReaderDelegate? _boundedReaderDelegate;
    private int _wireWriteTraceCount;
    private int _wireReadTraceCount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort SettingsClampHalfDelegate(float value, float min, float max, IntPtr fieldName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr InventoryAmountFormatterDelegate(int amount, byte formattingMode);

    // Generated bounded integer writer:
    //   writer, min, max, value, MethodInfo* -> bits written
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BoundedIntWriterDelegate(IntPtr writer, int min, int max, int value, IntPtr methodInfo);

    // Generated bounded integer reader:
    //   reader, min, max, MethodInfo* -> reconstructed value
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BoundedIntReaderDelegate(IntPtr reader, int min, int max, IntPtr methodInfo);

    [DllImport("GameAssembly.dll", EntryPoint = "il2cpp_string_new_utf16", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Il2CppStringNewUtf16(IntPtr text, int length);

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

        // Fix the 4095/4096 visible-count limitation in the active Burst AOT
        // inventory replication path. The current tested build has three inventory
        // ghost layouts, duplicated across two Burst compilation sets.
        try
        {
            _burstPatchRole = InstallBurstInventoryWirePatch();
            Log.LogInfo(
                $"Burst InventoryBuffer extended-count patch enabled as {_burstPatchRole} " +
                $"({VanillaInventoryBitWidth} -> {ExtendedInventoryBitWidth} bits).");
        }
        catch (Exception ex)
        {
            try
            {
                RestoreBurstInventoryWirePatch();
            }
            catch (Exception restoreEx)
            {
                Log.LogWarning($"Error while rolling back Burst inventory patch: {restoreEx}");
            }

            _burstPatchRole = BurstPatchRole.None;
            Log.LogError(
                "Failed to install the Burst InventoryBuffer extended-count patch. " +
                "The stack multiplier hook can still work, but inventory counts above 4095 may remain visually capped. " +
                $"Details: {ex}");
        }

        // RefreshData sends Data.Stacks through a dedicated amount formatter before
        // assigning the inventory label. Bypass that formatter only for values above
        // the vanilla 4095 ceiling so the inventory can show the exact replicated
        // Int32 count. Values at/below 4095 continue through the original formatter.
        if (_burstPatchRole == BurstPatchRole.Client)
        {
            try
            {
                InstallInventoryAmountFormatterHook();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to install inventory amount formatter hook: {ex}");
            }
        }
    }

    public override bool Unload()
    {
        try
        {
            RestoreBurstInventoryWirePatch();
            _burstPatchRole = BurstPatchRole.None;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Error while restoring Burst InventoryBuffer wire patch: {ex}");
        }

        try
        {
            _amountFormatterDetour?.Dispose();
            _amountFormatterDetour = null;
            _amountFormatterOriginal = null;
            _amountFormatterDelegate = null;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Error while removing inventory amount formatter detour: {ex}");
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

    private unsafe BurstPatchRole InstallBurstInventoryWirePatch()
    {
        RestoreBurstInventoryWirePatch();

        ProcessModule burstModule = GetBurstGeneratedModule();

        List<IntPtr> serverClampA = FindExecutablePatternMatches(
            burstModule,
            BurstServerClampPatternA,
            BurstServerClampPatternAMask);

        List<IntPtr> serverClampB = FindExecutablePatternMatches(
            burstModule,
            BurstServerClampPatternB,
            BurstServerClampPatternBMask);

        List<IntPtr> clientSecondReads = FindExecutablePatternMatches(
            burstModule,
            BurstClientSecondReadPattern,
            BurstClientSecondReadPatternMask);

        bool looksLikeServer =
            serverClampA.Count == ExpectedBurstServerClampMatchesPerShape &&
            serverClampB.Count == ExpectedBurstServerClampMatchesPerShape;

        bool hasNoServerClampSignatures =
            serverClampA.Count == 0 &&
            serverClampB.Count == 0;

        bool looksLikeClient =
            hasNoServerClampSignatures &&
            clientSecondReads.Count == ExpectedBurstClientDecoderPairs;

        if (looksLikeServer)
        {
            InstallBurstServerSerializerPatch(serverClampA, serverClampB);
            Log.LogInfo(
                $"Burst server inventory serializer patch verified: " +
                $"{serverClampA.Count + serverClampB.Count} inventory fields, {_burstWirePatches.Count} immediate edits.");
            return BurstPatchRole.Server;
        }

        if (looksLikeClient)
        {
            InstallBurstClientDeserializerPatch(clientSecondReads);
            Log.LogInfo(
                $"Burst client inventory deserializer patch verified: " +
                $"{clientSecondReads.Count * 2} inventory fields, {_burstWirePatches.Count} immediate edits.");
            return BurstPatchRole.Client;
        }

        throw new InvalidOperationException(
            $"Unrecognized lib_burst_generated.dll layout. " +
            $"Server clamp A={serverClampA.Count}, server clamp B={serverClampB.Count}, " +
            $"client second-read={clientSecondReads.Count}. Refusing to patch.");
    }

    private void InstallBurstServerSerializerPatch(
        List<IntPtr> clampShapeA,
        List<IntPtr> clampShapeB)
    {
        var candidates = new List<BurstMemoryPatch>(60);

        foreach (IntPtr clamp in clampShapeA)
        {
            AddBurstServerFieldPatch(
                candidates,
                clamp,
                firstMaximumImmediateOffset: 2,
                secondMaximumImmediateOffset: 7,
                "shape-A");
        }

        foreach (IntPtr clamp in clampShapeB)
        {
            AddBurstServerFieldPatch(
                candidates,
                clamp,
                firstMaximumImmediateOffset: 3,
                secondMaximumImmediateOffset: 8,
                "shape-B");
        }

        if (candidates.Count != 60)
        {
            throw new InvalidOperationException(
                $"Expected 60 Burst server immediate edits, generated {candidates.Count}. Refusing to patch.");
        }

        ValidateAndApplyBurstPatches(candidates);
    }

    private void AddBurstServerFieldPatch(
        List<BurstMemoryPatch> candidates,
        IntPtr clamp,
        int firstMaximumImmediateOffset,
        int secondMaximumImmediateOffset,
        string shapeName)
    {
        // Each field performs:
        //   reserve/check N bits
        //   clamp 0..4095
        //   write N bits
        //   advance bit position by N
        //
        // All four width uses and both upper-bound immediates must move together.
        List<IntPtr> preWidths = FindPatternMatchesInRange(
            IntPtr.Add(clamp, -0x30),
            0x30,
            BurstBitWidthMovEdx12,
            BurstBitWidthMovEdx12Mask);

        if (preWidths.Count != 1)
        {
            throw new InvalidOperationException(
                $"Burst server {shapeName} field at 0x{clamp.ToInt64():X}: " +
                $"expected exactly one pre-write 12-bit width, found {preWidths.Count}.");
        }

        List<IntPtr> postWidths = FindPatternMatchesInRange(
            clamp,
            0x70,
            BurstBitWidthMovEdx12,
            BurstBitWidthMovEdx12Mask);

        if (postWidths.Count != 1)
        {
            throw new InvalidOperationException(
                $"Burst server {shapeName} field at 0x{clamp.ToInt64():X}: " +
                $"expected exactly one writer 12-bit width, found {postWidths.Count}.");
        }

        IntPtr bitPositionImmediate = FindBurstServerBitPositionImmediate(clamp);

        candidates.Add(BurstMemoryPatch.Int32(
            IntPtr.Add(preWidths[0], 1),
            VanillaInventoryBitWidth,
            ExtendedInventoryBitWidth,
            $"Burst server {shapeName} pre-write bit width"));

        candidates.Add(BurstMemoryPatch.Int32(
            IntPtr.Add(clamp, firstMaximumImmediateOffset),
            VanillaInventoryWireMaximum,
            ExtendedInventoryWireMaximum,
            $"Burst server {shapeName} compare maximum"));

        candidates.Add(BurstMemoryPatch.Int32(
            IntPtr.Add(clamp, secondMaximumImmediateOffset),
            VanillaInventoryWireMaximum,
            ExtendedInventoryWireMaximum,
            $"Burst server {shapeName} clamp maximum"));

        candidates.Add(BurstMemoryPatch.Int32(
            IntPtr.Add(postWidths[0], 1),
            VanillaInventoryBitWidth,
            ExtendedInventoryBitWidth,
            $"Burst server {shapeName} writer bit width"));

        candidates.Add(BurstMemoryPatch.Byte(
            bitPositionImmediate,
            (byte)VanillaInventoryBitWidth,
            (byte)ExtendedInventoryBitWidth,
            $"Burst server {shapeName} bit-position advance"));
    }

    private static IntPtr FindBurstServerBitPositionImmediate(IntPtr clamp)
    {
        // Observed generated encodings:
        //   add dword ptr [r14+18h], 0Ch
        //   add dword ptr [rsi+18h], 0Ch
        //   add dword ptr [rdi+18h], 0Ch
        byte[][] patterns =
        {
            new byte[] { 0x41, 0x83, 0x46, 0x18, 0x0C },
            new byte[] { 0x83, 0x46, 0x18, 0x0C },
            new byte[] { 0x83, 0x47, 0x18, 0x0C }
        };

        var immediates = new HashSet<IntPtr>();

        foreach (byte[] pattern in patterns)
        {
            bool[] mask = CreateAllTrueMask(pattern.Length);
            List<IntPtr> matches = FindPatternMatchesInRange(clamp, 0x70, pattern, mask);

            foreach (IntPtr match in matches)
            {
                int immediateOffset = pattern.Length - 1;
                immediates.Add(IntPtr.Add(match, immediateOffset));
            }
        }

        if (immediates.Count != 1)
        {
            throw new InvalidOperationException(
                $"Burst server field at 0x{clamp.ToInt64():X}: " +
                $"expected exactly one 12-bit bit-position advance, found {immediates.Count}.");
        }

        foreach (IntPtr immediate in immediates)
            return immediate;

        throw new InvalidOperationException("Unreachable Burst bit-position lookup state.");
    }

    private void InstallBurstClientDeserializerPatch(List<IntPtr> secondReads)
    {
        var candidates = new List<BurstMemoryPatch>(24);

        secondReads.Sort((left, right) => left.ToInt64().CompareTo(right.ToInt64()));

        for (int i = 0; i < secondReads.Count; i++)
        {
            IntPtr secondRead = secondReads[i];

            List<IntPtr> firstReads = FindPatternMatchesInRange(
                IntPtr.Add(secondRead, -BurstClientFirstReadSearchBack),
                BurstClientFirstReadSearchBack,
                BurstClientFirstReadPattern,
                BurstClientFirstReadPatternMask);

            if (firstReads.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Burst client decoder pair #{i + 1}: expected exactly one matching first 12-bit read " +
                    $"within 0x{BurstClientFirstReadSearchBack:X} bytes, found {firstReads.Count}.");
            }

            IntPtr firstRead = firstReads[0];
            long distance = secondRead.ToInt64() - firstRead.ToInt64();
            if (distance < 0xE0 || distance > 0xF0)
            {
                throw new InvalidOperationException(
                    $"Burst client decoder pair #{i + 1}: unexpected field spacing 0x{distance:X}. Refusing to patch.");
            }

            candidates.Add(BurstMemoryPatch.Int32(
                IntPtr.Add(firstRead, BurstClientFirstReadWidthImmediateOffset),
                VanillaInventoryBitWidth,
                ExtendedInventoryBitWidth,
                $"Burst client pair #{i + 1} first-field reader width"));

            candidates.Add(BurstMemoryPatch.Byte(
                IntPtr.Add(firstRead, BurstClientFirstReadBitPositionImmediateOffset),
                (byte)VanillaInventoryBitWidth,
                (byte)ExtendedInventoryBitWidth,
                $"Burst client pair #{i + 1} first-field bit-position advance"));

            candidates.Add(BurstMemoryPatch.Int32(
                IntPtr.Add(secondRead, BurstClientSecondReadWidthImmediateOffset),
                VanillaInventoryBitWidth,
                ExtendedInventoryBitWidth,
                $"Burst client pair #{i + 1} second-field reader width"));

            candidates.Add(BurstMemoryPatch.Byte(
                IntPtr.Add(secondRead, BurstClientSecondReadBitPositionImmediateOffset),
                (byte)VanillaInventoryBitWidth,
                (byte)ExtendedInventoryBitWidth,
                $"Burst client pair #{i + 1} second-field bit-position advance"));
        }

        if (candidates.Count != 24)
        {
            throw new InvalidOperationException(
                $"Expected 24 Burst client immediate edits, generated {candidates.Count}. Refusing to patch.");
        }

        ValidateAndApplyBurstPatches(candidates);
    }

    private void ValidateAndApplyBurstPatches(List<BurstMemoryPatch> candidates)
    {
        var uniqueAddresses = new HashSet<IntPtr>();

        foreach (BurstMemoryPatch patch in candidates)
        {
            if (!uniqueAddresses.Add(patch.Address))
            {
                throw new InvalidOperationException(
                    $"Duplicate Burst patch address 0x{patch.Address.ToInt64():X} detected. Refusing to patch.");
            }

            byte[] current = ReadBytes(patch.Address, patch.OriginalBytes.Length);
            if (!BytesEqual(current, patch.OriginalBytes))
            {
                throw new InvalidOperationException(
                    $"Unexpected bytes at 0x{patch.Address.ToInt64():X} for {patch.Description}. Refusing to patch.");
            }
        }

        _burstWirePatches.AddRange(candidates);

        try
        {
            foreach (BurstMemoryPatch patch in candidates)
                WriteProtectedBytes(patch.Address, patch.ReplacementBytes);
        }
        catch
        {
            RestoreBurstInventoryWirePatch();
            throw;
        }
    }

    private void RestoreBurstInventoryWirePatch()
    {
        if (_burstWirePatches.Count == 0)
            return;

        Exception? firstError = null;

        for (int i = _burstWirePatches.Count - 1; i >= 0; i--)
        {
            BurstMemoryPatch patch = _burstWirePatches[i];

            try
            {
                byte[] current = ReadBytes(patch.Address, patch.ReplacementBytes.Length);

                if (BytesEqual(current, patch.ReplacementBytes))
                {
                    WriteProtectedBytes(patch.Address, patch.OriginalBytes);
                }
                else if (!BytesEqual(current, patch.OriginalBytes))
                {
                    Log.LogWarning(
                        $"Not restoring Burst patch '{patch.Description}' at 0x{patch.Address.ToInt64():X}; " +
                        "current bytes are neither VStack's replacement nor the original bytes.");
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        if (firstError is null)
            _burstWirePatches.Clear();

        if (firstError is not null)
            throw firstError;
    }

    private static ProcessModule GetBurstGeneratedModule()
    {
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (string.Equals(module.ModuleName, "lib_burst_generated.dll", StringComparison.OrdinalIgnoreCase))
                return module;
        }

        throw new InvalidOperationException("lib_burst_generated.dll is not loaded.");
    }

    private unsafe List<IntPtr> FindExecutablePatternMatches(
        ProcessModule module,
        byte[] pattern,
        bool[] mask)
    {
        if (pattern.Length == 0 || pattern.Length != mask.Length)
            throw new ArgumentException("Pattern and mask must be non-empty and the same length.");

        byte* imageBase = (byte*)module.BaseAddress;
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

    private static bool[] CreateAllTrueMask(int length)
    {
        var mask = new bool[length];
        for (int i = 0; i < length; i++)
            mask[i] = true;
        return mask;
    }

    private static byte[] ReadBytes(IntPtr address, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(address, bytes, 0, length);
        return bytes;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private static void WriteProtectedBytes(IntPtr address, byte[] bytes)
    {
        UIntPtr size = new UIntPtr((uint)bytes.Length);

        if (!VirtualProtect(address, size, PageExecuteReadWrite, out uint oldProtect))
        {
            throw new InvalidOperationException(
                $"VirtualProtect(RWX) failed at 0x{address.ToInt64():X}; Win32 error {Marshal.GetLastWin32Error()}.");
        }

        Exception? writeError = null;

        try
        {
            Marshal.Copy(bytes, 0, address, bytes.Length);

            byte[] verify = ReadBytes(address, bytes.Length);
            if (!BytesEqual(verify, bytes))
            {
                throw new InvalidOperationException(
                    $"Burst memory verification failed at 0x{address.ToInt64():X}.");
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
                writeError ??= new InvalidOperationException(
                    $"VirtualProtect(restore) failed at 0x{address.ToInt64():X}; Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }

        if (writeError is not null)
            throw writeError;
    }

    private void InstallWireDiagnostics()
    {
        if (_boundedWriterDetour is not null || _boundedReaderDetour is not null)
            return;

        if (_inventoryWriterTarget == IntPtr.Zero || _inventoryReaderTarget == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Inventory wire diagnostic targets were not resolved while validating the wire patch.");
        }

        _wireWriteTraceCount = 0;
        _wireReadTraceCount = 0;

        BoundedIntWriterDelegate writerDelegate = BoundedIntWriterDetour;
        _boundedWriterDelegate = writerDelegate;
        _boundedWriterDetour = INativeDetour.CreateAndApply<BoundedIntWriterDelegate>(
            _inventoryWriterTarget,
            writerDelegate,
            out var writerOriginal);
        _boundedWriterOriginal = writerOriginal;

        BoundedIntReaderDelegate readerDelegate = BoundedIntReaderDetour;
        _boundedReaderDelegate = readerDelegate;
        _boundedReaderDetour = INativeDetour.CreateAndApply<BoundedIntReaderDelegate>(
            _inventoryReaderTarget,
            readerDelegate,
            out var readerOriginal);
        _boundedReaderOriginal = readerOriginal;

        Log.LogInfo($"V7 diagnostic hook: raw bounded writer at 0x{_inventoryWriterTarget.ToInt64():X}.");
        Log.LogInfo($"V7 diagnostic hook: raw bounded reader at 0x{_inventoryReaderTarget.ToInt64():X}.");
        Log.LogWarning(
            "VStack v7 RAW wire tracing is ENABLED. It logs the first bounded reader/writer calls without assuming their values. Reproduce one >4095 stack, open inventory, then send LogOutput.log.");
    }

    private void RemoveWireDiagnostics()
    {
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

        int traceNumber = Interlocked.Increment(ref _wireWriteTraceCount);
        if (traceNumber <= DiagnosticTraceLimitPerStage)
        {
            Log.LogWarning(
                $"VSTACK-V7 RAW-WRITE PRE #{traceNumber}: writer=0x{writer.ToInt64():X}, min={min}, max={max}, value={value}, method=0x{methodInfo.ToInt64():X}.");
        }

        int result = original(writer, min, max, value, methodInfo);

        if (traceNumber <= DiagnosticTraceLimitPerStage)
        {
            Log.LogWarning(
                $"VSTACK-V7 RAW-WRITE POST #{traceNumber}: result={result}.");
        }

        return result;
    }

    private int BoundedIntReaderDetour(IntPtr reader, int min, int max, IntPtr methodInfo)
    {
        BoundedIntReaderDelegate? original = _boundedReaderOriginal;
        if (original is null)
            return 0;

        int traceNumber = Interlocked.Increment(ref _wireReadTraceCount);
        if (traceNumber <= DiagnosticTraceLimitPerStage)
        {
            Log.LogWarning(
                $"VSTACK-V7 RAW-READ PRE #{traceNumber}: reader=0x{reader.ToInt64():X}, min={min}, max={max}, method=0x{methodInfo.ToInt64():X}.");
        }

        int value = original(reader, min, max, methodInfo);

        if (traceNumber <= DiagnosticTraceLimitPerStage)
        {
            Log.LogWarning(
                $"VSTACK-V7 RAW-READ POST #{traceNumber}: value={value}.");
        }

        return value;
    }

    private void InstallInventoryAmountFormatterHook()
    {
        if (_amountFormatterDetour is not null)
            return;

        IntPtr target = FindInventoryAmountFormatter();
        if (target == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "ItemGridSelectionEntry.RefreshData amount-formatter call site was not found.");
        }

        InventoryAmountFormatterDelegate detourDelegate = InventoryAmountFormatterDetour;
        _amountFormatterDelegate = detourDelegate;
        _amountFormatterDetour = INativeDetour.CreateAndApply<InventoryAmountFormatterDelegate>(
            target,
            detourDelegate,
            out var original);
        _amountFormatterOriginal = original;

        Log.LogInfo($"Hooked inventory amount formatter at 0x{target.ToInt64():X} for exact counts above 4095.");
    }

    private IntPtr InventoryAmountFormatterDetour(int amount, byte formattingMode)
    {
        InventoryAmountFormatterDelegate? original = _amountFormatterOriginal;
        if (original is null)
            return IntPtr.Zero;

        // RefreshData uses formattingMode == 1 for the stack-count label. Other
        // callers of this shared formatter use mode 0, so leave those unrelated UI
        // paths completely unchanged.
        if (formattingMode != 1)
            return original(amount, formattingMode);

        try
        {
            // Seeing exactly 4095 here proves the value is still capped before the
            // text formatter. Log that once so the next investigation has a
            // definitive breakpoint without touching the large UI ABI.
            if (amount == VanillaInventoryWireMaximum)
            {
                if (!_loggedFormatterAtVanillaCap)
                {
                    _loggedFormatterAtVanillaCap = true;
                    Log.LogWarning(
                        "Inventory stack formatter received exactly 4095. " +
                        "If the real stack is larger, the remaining cap is upstream of text formatting.");
                }

                return original(amount, formattingMode);
            }

            if (amount > VanillaInventoryWireMaximum)
            {
                if (!_loggedFormatterOverride)
                {
                    _loggedFormatterOverride = true;
                    Log.LogInfo(
                        $"Inventory stack formatter received {amount}; replacing vanilla formatted text with exact decimal count.");
                }

                IntPtr exactText = CreateIl2CppString(
                    amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (exactText != IntPtr.Zero)
                    return exactText;
            }
        }
        catch (Exception ex)
        {
            // Never allow a managed exception to escape back through the native UI
            // call frame. Fall back to V Rising's original formatter instead.
            if (!_loggedFormatterFailure)
            {
                _loggedFormatterFailure = true;
                Log.LogError($"Exact inventory-count formatter failed; using vanilla text: {ex}");
            }
        }

        return original(amount, formattingMode);
    }

    private static unsafe IntPtr CreateIl2CppString(string text)
    {
        fixed (char* chars = text)
        {
            return Il2CppStringNewUtf16((IntPtr)chars, text.Length);
        }
    }

    private IntPtr FindInventoryAmountFormatter()
    {
        List<IntPtr> callSites = FindExecutablePatternMatches(
            InventoryAmountFormatterCallSitePattern,
            InventoryAmountFormatterCallSitePatternMask);

        if (callSites.Count == 0)
            return IntPtr.Zero;

        if (callSites.Count != 1)
        {
            throw new InvalidOperationException(
                $"Inventory amount formatter call-site signature was not unique ({callSites.Count} matches). Refusing to hook.");
        }

        IntPtr callInstruction = IntPtr.Add(callSites[0], InventoryAmountFormatterCallInstructionOffset);
        if (Marshal.ReadByte(callInstruction) != 0xE8)
        {
            throw new InvalidOperationException(
                $"Expected CALL rel32 at 0x{callInstruction.ToInt64():X}; refusing to resolve formatter target.");
        }

        int relative = Marshal.ReadInt32(IntPtr.Add(callInstruction, 1));
        long targetAddress = checked(callInstruction.ToInt64() + 5L + relative);
        IntPtr target = new IntPtr(targetAddress);

        // Verify the resolved target begins with one of the two prologues generated
        // for this helper in the supplied client/server builds. This keeps the hook
        // fail-closed if a game update changes the call target.
        byte first = Marshal.ReadByte(target, 0);
        byte second = Marshal.ReadByte(target, 1);
        if (first != 0x48 || second != 0x89)
        {
            throw new InvalidOperationException(
                $"Resolved inventory amount formatter target 0x{target.ToInt64():X} has an unexpected prologue.");
        }

        return target;
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

        List<IntPtr> serializerAnchorsA = FindExecutablePatternMatches(
            InventorySerializerPatternA,
            InventorySerializerPatternAMask);

        List<IntPtr> serializerAnchorsB = FindExecutablePatternMatches(
            InventorySerializerPatternB,
            InventorySerializerPatternBMask);

        if (serializerAnchorsA.Count != ExpectedInventorySerializerVariantACount)
        {
            throw new InvalidOperationException(
                $"Expected exactly {ExpectedInventorySerializerVariantACount} InventoryBuffer serializer variant-A implementations, " +
                $"found {serializerAnchorsA.Count}. Refusing to patch.");
        }

        if (serializerAnchorsB.Count != ExpectedInventorySerializerVariantBCount)
        {
            throw new InvalidOperationException(
                $"Expected exactly {ExpectedInventorySerializerVariantBCount} InventoryBuffer serializer variant-B implementation, " +
                $"found {serializerAnchorsB.Count}. Refusing to patch.");
        }

        var serializerAnchors = new List<IntPtr>(ExpectedInventorySerializerImplementations);
        serializerAnchors.AddRange(serializerAnchorsA);
        serializerAnchors.AddRange(serializerAnchorsB);

        var uniqueSerializerAnchors = new HashSet<IntPtr>();
        foreach (IntPtr anchor in serializerAnchors)
        {
            if (!uniqueSerializerAnchors.Add(anchor))
            {
                throw new InvalidOperationException(
                    $"Duplicate InventoryBuffer serializer anchor 0x{anchor.ToInt64():X} detected. Refusing to patch.");
            }
        }

        serializerAnchors.Sort((left, right) => left.ToInt64().CompareTo(right.ToInt64()));
        var candidates = new List<MemoryPatch>(ExpectedInventorySerializerImplementations * 3);
        var writerTargets = new HashSet<IntPtr>();
        var readerTargets = new HashSet<IntPtr>();

        // Both serializer shapes call the same bounded-int writer, but the call
        // instruction is four bytes later in variant B because of register allocation.
        foreach (IntPtr anchor in serializerAnchorsA)
        {
            writerTargets.Add(ResolveRelativeCallTarget(
                IntPtr.Add(anchor, SerializerWriterCallOffsetVariantA),
                "InventoryBuffer serializer variant-A bounded writer"));
        }

        foreach (IntPtr anchor in serializerAnchorsB)
        {
            writerTargets.Add(ResolveRelativeCallTarget(
                IntPtr.Add(anchor, SerializerWriterCallOffsetVariantB),
                "InventoryBuffer serializer variant-B bounded writer"));
        }

        for (int implementationIndex = 0; implementationIndex < serializerAnchors.Count; implementationIndex++)
        {
            IntPtr serializerAnchor = serializerAnchors[implementationIndex];
            int displayIndex = implementationIndex + 1;

            IntPtr serializerMaximum = IntPtr.Add(serializerAnchor, SerializerMaximumImmediateOffset);
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
                readerTargets.Add(ResolveRelativeCallTarget(
                    IntPtr.Add(deserializerSite, DeserializerReaderCallOffset),
                    $"InventoryBuffer deserializer #{displayIndex} bounded reader"));
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
                $"Expected all InventoryBuffer serializers to share one bounded writer target; found {writerTargets.Count}.");
        }

        if (readerTargets.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected all InventoryBuffer deserializers to share one bounded reader target; found {readerTargets.Count}.");
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

    private enum BurstPatchRole
    {
        None = 0,
        Client = 1,
        Server = 2
    }

    private sealed class BurstMemoryPatch
    {
        private BurstMemoryPatch(
            IntPtr address,
            byte[] originalBytes,
            byte[] replacementBytes,
            string description)
        {
            Address = address;
            OriginalBytes = originalBytes;
            ReplacementBytes = replacementBytes;
            Description = description;
        }

        public IntPtr Address { get; }
        public byte[] OriginalBytes { get; }
        public byte[] ReplacementBytes { get; }
        public string Description { get; }

        public static BurstMemoryPatch Int32(
            IntPtr address,
            int originalValue,
            int replacementValue,
            string description)
        {
            return new BurstMemoryPatch(
                address,
                BitConverter.GetBytes(originalValue),
                BitConverter.GetBytes(replacementValue),
                description);
        }

        public static BurstMemoryPatch Byte(
            IntPtr address,
            byte originalValue,
            byte replacementValue,
            string description)
        {
            return new BurstMemoryPatch(
                address,
                new[] { originalValue },
                new[] { replacementValue },
                description);
        }
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
