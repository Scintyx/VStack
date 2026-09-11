# VStack

VStack is an open-source, managed-only **BepInEx 6 IL2CPP** plugin for **V Rising** that increases `InventoryStacksModifier` beyond the normal x3 limitation and fixes the vanilla 4095 inventory replication/display ceiling for extended stacks.

The stack multiplier is configurable. The default is **x1000**.

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

VStack accepts positive multiplier values from **0.01 to 65504**. `65504` is the largest finite half-precision value supported by the game setting path used by the stack-setting hook. Invalid, zero, negative, NaN, or infinite values fall back to the default x1000; values above 65504 are safely reduced to 65504.

Restart V Rising / the server after editing the config file.

## What VStack changes

VStack changes only:

```text
InventoryStacksModifier
```

It does **not** uncap or modify unrelated server settings such as drop rates, refinement rates, resource yield, durability, castle settings, or crafting rates.

V Rising's active inventory replication path encodes `InventoryBuffer.Amount` and `MaxAmountOverride` as 12-bit values, which limits the replicated count to 4095. VStack widens only the validated inventory-specific Burst AOT fields to 31 bits, allowing the real stack amount to reach the client inventory UI.

## Important: install on server and clients

The extended-count fix changes the `InventoryBuffer` network bit width. The **same VStack build must be installed on the server/host and every connecting client**.

Do not mix a VStack-enabled server with vanilla clients, or a VStack-enabled client with a vanilla server, while the extended-count patch is active.

For Host & Play, install VStack in the BepInEx environment used by the host game. For a dedicated server, install the same VStack build on the dedicated server and on all connecting players.

## Managed-only implementation

VStack ships only one runtime mod assembly:

```text
BepInEx/plugins/VStack/VStack.dll
```

There is no custom `VStack.Native.dll`, no MinHook binary, and no `version.dll` proxy.

The stack-setting hook uses BepInEx's own `BepInEx.Unity.IL2CPP.Hook.INativeDetour.CreateAndApply` API from managed C#.

The extended-count fix is also implemented inside `VStack.dll`. Production signature matching locates the required code in the already-loaded `lib_burst_generated.dll`, validates the expected vanilla bytes and match counts, changes only those verified inventory fields in process memory, and restores them on plugin unload.

No game DLL is modified on disk.

## Requirements

- V Rising (Windows x64)
- BepInExPack V Rising / BepInEx 6 IL2CPP
- VStack installed on the server/host and every connecting client
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

GitHub Actions builds both the managed DLL and a Thunderstore-ready ZIP.

## Safety / compatibility

The `SettingsClamp::Half` target is accepted only when its production signature is found exactly once.

The Burst inventory patch is also fail-closed. On the current supported dedicated-server Burst layout it requires exactly 6 matches of each validated server clamp shape, representing 12 inventory fields. On the client layout it requires exactly 6 paired decoder functions, also representing 12 inventory fields. Every target byte must still contain the expected vanilla 12-bit or 4095 value before VStack writes anything.

If a V Rising update changes these layouts, VStack refuses the affected patch instead of modifying an unknown address.

Do not combine VStack with another mod that modifies the same `SettingsClamp::Half` function or the same Burst `InventoryBuffer` serialization path unless compatibility has been verified.

## Source transparency

The Thunderstore package includes the exact `Plugin.cs` and `VStack.csproj` used by CI plus `Source/BUILD_COMMIT.txt`, which contains the Git commit hash that produced the packaged DLL.

## License

MIT. See `LICENSE`.

## Acknowledgements

See `ACKNOWLEDGEMENTS.md`.
