# Source review notes

VStack is intentionally implemented as a single managed C# BepInEx plugin.

Runtime binary produced by CI:

```text
BepInEx/plugins/VStack/VStack.dll
```

Authoritative source for that assembly:

```text
src/VStack/Plugin.cs
src/VStack/VStack.csproj
```

The Thunderstore build also places those exact files under `Source/` and writes the Git commit hash to `Source/BUILD_COMMIT.txt` so reviewers can match a release package to its repository revision.

No custom native DLL, MinHook binary, or `version.dll` proxy is included.

## Runtime modifications

VStack performs two narrowly scoped runtime modifications from managed C#:

1. **Stack multiplier** — BepInEx `INativeDetour.CreateAndApply` detours `SettingsClamp::Half`. The detour checks the IL2CPP field name and changes values only when the field is exactly `InventoryStacksModifier`. All unrelated settings are passed to the original function unchanged.
2. **Extended inventory count replication** — a pattern scanner locates both generated `InventoryBuffer` snapshot serializer implementations present in the current V Rising build. For each implementation, the code validates one shared serializer `0x0FFF` (4095) bound and exactly two corresponding `0x0FFF` bounds in the local deserializer region. Only those six validated 32-bit immediates are changed to `Int32.MaxValue`. Original values are restored on unload.

The second modification is necessary because V Rising's underlying inventory/snapshot amount is a 32-bit integer, but the generated inventory wire serializer normally bounds `Amount` and `MaxAmountOverride` to 4095 before the inventory reaches the client UI.

Because changing that bound changes this snapshot field's network bit width, the same VStack build is required on the server and all connecting clients.

Both scanners fail closed if their expected signatures/counts/values do not match. No GameAssembly file is modified on disk.
