# Changelog

## 1.0.0

- Renamed the public project and package to **VStack**.
- Managed-only BepInEx plugin; no custom native helper DLLs.
- Uses `BepInEx.Unity.IL2CPP.Hook.INativeDetour.CreateAndApply` directly from C#.
- Adds editable `BepInEx/config/VStack.cfg`.
- Stack multiplier defaults to **x1000** and is user-configurable.
- Supports positive multipliers from **0.01 to 65504**.
- Only targets `InventoryStacksModifier`; unrelated server settings remain untouched.
- Includes complete C# source in the public repository and Thunderstore package.
- Includes deterministic build settings and GitHub Actions packaging.
- Documents the remaining vanilla 4095/4096 inventory-display limitation.
