# Source review notes

VStacks is intentionally implemented as a single managed C# BepInEx plugin.

Runtime binary produced by CI:

```text
BepInEx/plugins/VStack/VStack.dll
```

Authoritative source for that assembly:

```text
src/VStack/Plugin.cs
src/VStack/VStack.csproj
```

The Thunderstore build places those exact files under `Source/` and writes the Git commit hash to `Source/BUILD_COMMIT.txt` so reviewers can match a release package to its repository revision.

No custom native DLL, MinHook binary, or `version.dll` proxy is included.

## Runtime modifications

VStacks performs exactly two production runtime modifications from managed C#:

1. **Stack multiplier** — BepInEx `INativeDetour.CreateAndApply` detours `SettingsClamp::Half`. The detour compares the IL2CPP field name and changes the value only when the field is exactly `InventoryStacksModifier`. Every unrelated setting passes through to the original function unchanged.
2. **Extended inventory count replication** — production signature matching locates the active inventory replication code inside the loaded Unity Burst AOT `lib_burst_generated.dll`. The server side validates 12 inventory fields and changes their 12-bit reserve/write widths to 31 bits, raises the two 4095 upper-bound immediates per field to `Int32.MaxValue`, and changes the matching bit-position advances. The client side validates the matching 12 reads and changes their read widths and bit-position advances from 12 to 31 bits.

The second modification is required because the underlying inventory amount is a 32-bit integer while the active Burst wire path normally represents these inventory fields with 12 bits, limiting the replicated value to 4095.

Because the network bit width changes, the same VStacks release is required on the server/host and every connecting client.

## Release cleanliness

The v1.0.0 production source contains no temporary diagnostic detours, raw wire tracing, UI formatter override, inventory UI probe, or development test hook.

The remaining executable-code searches are production signature locators required to find the supported game functions without hard-coded process addresses. They validate exact match counts and expected original bytes and fail closed when the supported layout is not present.

No `GameAssembly.dll` or Burst DLL is modified on disk. VStacks changes only loaded process memory and restores its Burst edits on plugin unload.

## Package naming

The public Thunderstore package is named **VStacks**. The internal runtime assembly and compatibility identifiers intentionally remain `VStack.dll`, `VStack.cfg`, namespace `VStack`, and plugin GUID `com.originera.vstack`. This rename does not change runtime behavior or compatibility.


## Cooperative settings-hook compatibility

Starting with VStacks 1.0.1, compatible OriginEra plugins may register a named multiplier provider through
`VStack.Plugin.RegisterSettingOverride(...)`. This avoids multiple plugins independently detouring the same
`SettingsClamp::Half` native function.

VStacks does not broadly uncap additional settings. An external setting changes only after an installed
plugin explicitly registers its exact IL2CPP field name. Registrations are removed when the external plugin
unloads, and VStacks clears all registrations on its own unload.
