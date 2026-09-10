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
