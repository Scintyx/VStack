# VStack

VStack is an open-source, managed-only **BepInEx 6 IL2CPP** plugin for **V Rising** that changes only `InventoryStacksModifier` and fixes the vanilla 4095 inventory-count replication limit for extended stacks.

The stack multiplier is user-configurable. The default is **x1000**.

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

VStack accepts positive multiplier values from **0.01 to 65504**. `65504` is the largest finite half-precision value supported by the game setting path used by the stack-setting hook. Values above that are safely reduced to 65504; invalid, zero, negative, NaN, or infinite values fall back to the default x1000.

Restart V Rising / the server after editing the config text file.

## What VStack changes

VStack changes only the stack setting:

```text
InventoryStacksModifier
```

It does **not** uncap or modify unrelated server settings such as drop rates, refinement rates, resource yield, durability, castle settings, or crafting rates.

VStack also patches only the generated `InventoryBuffer` snapshot amount bounds used for inventory replication. V Rising normally bounds those wire values to `4095` even though the underlying inventory amount is a 32-bit integer. VStack raises those inventory-specific wire bounds so the real quantity can reach the client inventory UI instead of being visually capped at 4095.

## Important: install on server and clients

The extended visible-count fix changes the generated `InventoryBuffer` network encoding. Therefore the same VStack build must be installed on:

- the server / host; and
- every client connecting to that server.

Do **not** use the extended-count build with a mixture of VStack and vanilla clients. A vanilla peer expects V Rising's original 4095-bounded inventory snapshot format.

For Host & Play, install VStack in the BepInEx environment used by the host game. For a dedicated server, install the same VStack build on the dedicated server and on all connecting players.

## Managed-only implementation

VStack does not ship a custom native DLL, MinHook, or `version.dll` proxy.

The stack-setting hook uses BepInEx's own `BepInEx.Unity.IL2CPP.Hook.INativeDetour` API from managed C#.

The extended-count fix is also implemented in the same managed `VStack.dll`: C# pattern scanning locates the generated `InventoryBuffer` serializer/deserializer bounds in the loaded `GameAssembly.dll`, validates the expected vanilla `0x0FFF` values, changes only those validated immediate values in memory, and restores them on plugin unload.

The current V Rising build contains two generated `InventoryBuffer` serializer/deserializer implementations. VStack validates and patches both. The scanner refuses to apply the count patch unless exactly two known serializer implementations are found and each has exactly two matching deserializer bounds in its expected local region.

Runtime installation contains one mod assembly:

```text
BepInEx/plugins/VStack/VStack.dll
```

The Thunderstore package also includes the exact C# source used to build it under `Source/`, plus the Git commit hash produced by GitHub Actions.

## Requirements

- V Rising (Windows x64)
- BepInExPack V Rising / BepInEx 6 IL2CPP
- VStack installed on the server/host and every connecting client for the extended visible-count protocol
- For Host & Play, the usual ServerLaunchFix setup if required by the server-side BepInEx environment

## Installation

Install through Thunderstore/r2modman, or copy:

```text
VStack.dll
```

to:

```text
BepInEx/plugins/VStack/
```

Install the same build on the server/host and every client.

Start the game/server once to generate `BepInEx/config/VStack.cfg`.

## Build

```bash
dotnet restore src/VStack/VStack.csproj --configfile nuget.config
dotnet build src/VStack/VStack.csproj -c Release --no-restore
```

GitHub Actions is included and builds both the managed DLL and a Thunderstore-ready ZIP.

## Safety / compatibility

The `SettingsClamp::Half` scanner only installs its detour when the target pattern is found **exactly once**.

The inventory wire patch separately requires exactly two known `InventoryBuffer` serializer implementations. For each implementation, VStack validates the shared serializer `0x0FFF` bound plus exactly two `0x0FFF` deserializer bounds. That is six validated immediate values total. If V Rising changes these generated functions, VStack refuses that patch rather than modifying an unknown address.

Do not combine VStack with another mod that changes the same `SettingsClamp::Half` function or the same generated `InventoryBuffer` snapshot serialization unless compatibility has been verified.

## License

MIT. See `LICENSE`.

## Acknowledgements

See `ACKNOWLEDGEMENTS.md`.
