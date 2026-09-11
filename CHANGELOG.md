# Changelog

## 1.0.0

- Initial public release of **VStack**.
- Managed-only BepInEx 6 IL2CPP plugin; no custom native helper DLL, MinHook, or `version.dll` proxy.
- Uses BepInEx `INativeDetour.CreateAndApply` for the stack-setting hook.
- Changes only `InventoryStacksModifier`; unrelated server settings pass through unchanged.
- Adds `BepInEx/config/VStack.cfg` with a configurable stack multiplier.
- Default multiplier is **x1000**.
- Supports positive multiplier values from **0.01 to 65504**.
- Fixes the vanilla 4095 inventory replication/display ceiling for extended stacks by widening the validated active Burst AOT `InventoryBuffer` wire fields from 12 bits to 31 bits.
- Requires the same VStack build on the server/host and all connecting clients because the inventory network bit width is changed.
- Uses fail-closed production signature validation and verifies original bytes before changing process memory.
- Restores Burst memory edits on plugin unload.
- Removed all development diagnostics, raw wire tracing, UI probes, and the temporary amount-formatter detour from the release source.
- Includes complete reviewer-facing C# source and `BUILD_COMMIT.txt` in the Thunderstore package.
