# Changelog

## 1.0.0

- Fixed GitHub Actions compile compatibility by using `System.IntPtr` instead of the native-integer `nint` alias for BepInEx native detour pointers.
- Renamed the public project and package to **VStack**.
- Managed-only BepInEx plugin; no custom native helper DLLs.
- Uses `BepInEx.Unity.IL2CPP.Hook.INativeDetour.CreateAndApply` directly from C#.
- Adds editable `BepInEx/config/VStack.cfg`.
- Stack multiplier defaults to **x1000** and is user-configurable.
- Supports positive multipliers from **0.01 to 65504**.
- Only targets `InventoryStacksModifier`; unrelated server settings remain untouched.
- Identified the vanilla 4095 visible-count limit in the generated `InventoryBuffer` network serializer/deserializer rather than the inventory text/UI formatter.
- Added a managed, pattern-scanned extended inventory-count wire patch that raises only the validated `InventoryBuffer` amount bounds from 4095 to the positive 32-bit range.
- Corrected the extended-count patch to cover both generated `InventoryBuffer` serializer/deserializer implementations present in the current game build. The first development attempt patched only one implementation, leaving the live player-inventory path capped at 4095.
- Added fail-closed validation for exactly two serializer implementations, two matching deserializer bounds per implementation, and all six original `0x0FFF` immediate values before writing memory.
- Restores the original inventory wire bounds on plugin unload.
- Extended visible counts require the same VStack build on the server and every connecting client.
- Includes complete C# source in the public repository and Thunderstore package.
- Includes deterministic build settings and GitHub Actions packaging.
