# VStack

VStack is an open-source, managed-only **BepInEx 6 IL2CPP** plugin for **V Rising** that changes only `InventoryStacksModifier`.

The multiplier is user-configurable. The default is **x1000**.

## Configuration

On first launch, VStack creates:

```text
BepInEx/config/VStack.cfg
```

Edit it with any text editor:

```ini
[Stacks]
Multiplier = 1000
```

Examples:

```ini
Multiplier = 10
Multiplier = 100
Multiplier = 500
Multiplier = 1000
Multiplier = 5000
```

VStack accepts positive multiplier values from **0.01 to 65504**. `65504` is the largest finite half-precision value supported by the game setting path used by this hook. Values above that are safely reduced to 65504; invalid, zero, negative, NaN, or infinite values fall back to the default x1000.

Restart V Rising / the server after editing the config text file.

## What VStack changes

Only:

```text
InventoryStacksModifier
```

It does **not** uncap or modify unrelated server settings such as drop rates, refinement rates, resource yield, durability, castle settings, or crafting rates.

## Managed-only hook

VStack does not ship a custom native DLL, MinHook, or `version.dll` proxy.

It uses BepInEx's own `BepInEx.Unity.IL2CPP.Hook.INativeDetour` API from managed C#. The pattern scanner and detour are both visible in `src/VStack/Plugin.cs`.

Runtime installation contains one mod assembly:

```text
BepInEx/plugins/VStack/VStack.dll
```

The Thunderstore package also includes the exact C# source used to build it under `Source/`, plus the Git commit hash produced by GitHub Actions.

## Current display limitation

The real server-side stack amount can exceed 4095/4096. V Rising's normal inventory slot counter may still visually stop at 4095/4096 even when the real quantity is higher. Dropped-world pickup text can show the full amount. This is a separate client replication/UI limitation and is not advertised as fixed by this release.

## Requirements

- V Rising (Windows x64)
- BepInExPack V Rising / BepInEx 6 IL2CPP
- For Host & Play, the usual ServerLaunchFix setup is required for server-side BepInEx plugins.

## Installation

Install through Thunderstore/r2modman, or copy:

```text
VStack.dll
```

to:

```text
BepInEx/plugins/VStack/
```

Start the game/server once to generate `BepInEx/config/VStack.cfg`.

## Build

```bash
dotnet restore src/VStack/VStack.csproj --configfile nuget.config
dotnet build src/VStack/VStack.csproj -c Release --no-restore
```

GitHub Actions is included and builds both the managed DLL and a Thunderstore-ready ZIP.

## Safety / compatibility

The signature scanner only installs the detour when the target pattern is found **exactly once**. If a V Rising update changes the target or makes the signature ambiguous, VStack refuses to hook and logs an error instead of patching an unknown address.

Do not combine VStack with another mod that detours the same `SettingsClamp::Half` function unless compatibility has been verified.

## License

MIT. See `LICENSE`.

## Acknowledgements

See `ACKNOWLEDGEMENTS.md`.
