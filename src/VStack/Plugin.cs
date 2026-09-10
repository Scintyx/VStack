using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    private const string TargetSettingName = "InventoryStacksModifier";
    private const uint ImageScnMemExecute = 0x20000000;

    private ConfigFile? _vStackConfig;
    private ConfigEntry<float>? _multiplier;
    private INativeDetour? _detour;
    private SettingsClampHalfDelegate? _original;
    private SettingsClampHalfDelegate? _detourDelegate;
    private bool _loggedFirstIntercept;
    private bool _loggedInvalidConfig;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort SettingsClampHalfDelegate(float value, float min, float max, nint fieldName);

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

        try
        {
            nint target = FindSettingsClampHalf();
            if (target == nint.Zero)
            {
                Log.LogError("SettingsClamp::Half signature was not found. The game may have updated; no patch was applied.");
                return;
            }

            SettingsClampHalfDelegate detourDelegate = SettingsClampHalfDetour;
            _detourDelegate = detourDelegate;
            _detour = INativeDetour.CreateAndApply<SettingsClampHalfDelegate>(target, detourDelegate, out var original);
            _original = original;

            Log.LogInfo($"{PluginName} {PluginVersion} loaded (managed-only BepInEx hook).");
            Log.LogInfo($"Hooked SettingsClamp::Half at 0x{target.ToInt64():X}.");
            Log.LogInfo($"Target setting: {TargetSettingName}; configured multiplier: x{GetMultiplier():0.###}.");
            Log.LogInfo($"Config file: {_vStackConfig.ConfigFilePath}");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to install SettingsClamp::Half detour: {ex}");
        }
    }

    public override bool Unload()
    {
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

    private ushort SettingsClampHalfDetour(float value, float min, float max, nint fieldName)
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

    private static unsafe bool IsIl2CppStringEqual(nint stringObject, string expected)
    {
        if (stringObject == nint.Zero)
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

    private unsafe nint FindSettingsClampHalf()
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

        nint found = nint.Zero;
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
                found = (nint)(start + offset);
            }
        }

        if (matches == 0)
            return nint.Zero;

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
}
