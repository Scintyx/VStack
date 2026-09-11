# Build and publish VStack

## Recommended GitHub flow

1. Commit the release source to the public `Scintyx/VStack` repository.
2. Push to `main`/`master`, push a `v*` tag, or manually run **Build VStack** from the Actions tab.
3. Confirm the `build` job completes successfully.
4. Download the Actions artifact named `VStack-Thunderstore-v1.0.0`.
5. Open the generated `VStack_Thunderstore_v1.0.0.zip` and confirm `Source/BUILD_COMMIT.txt` matches the Git commit used for the release.
6. Upload that generated ZIP to Thunderstore.

The workflow replaces `__REPOSITORY_URL__` and `__VERSION__` in the manifest template, then writes the exact Git commit hash into `Source/BUILD_COMMIT.txt`.

## Runtime vs source

BepInEx loads the compiled managed `VStack.dll`; `.cs` files are included for transparency/review and are not loaded directly by BepInEx.

The Thunderstore package contains:

```text
manifest.json
README.md
CHANGELOG.md
SOURCE_REVIEW.md
icon.png

BepInEx/plugins/VStack/VStack.dll

Source/Plugin.cs
Source/VStack.csproj
Source/BUILD_COMMIT.txt
```

There are no custom native DLLs in VStack. The production v1.0.0 source also contains no temporary diagnostic hooks, raw wire tracing, UI probes, or formatter override.

## CI validation

Before uploading artifacts, the workflow verifies that:

- all required Thunderstore/package files exist and are non-empty;
- `Source/Plugin.cs` and `Source/VStack.csproj` exactly match the source used by the build;
- `Source/BUILD_COMMIT.txt` equals the current GitHub commit SHA;
- `manifest.json` is valid JSON;
- `icon.png` is a valid 256x256 PNG;
- no old native helper/proxy files are present;
- no temporary diagnostic/probe code or diagnostic test files are present in the release source;
- the plugin version matches the workflow `MOD_VERSION`;
- `VStack.dll` is the only DLL shipped in the package;
- the generated ZIP passes an archive integrity test.

The workflow uses current Node.js 24-compatible official actions: `actions/checkout@v7`, `actions/setup-dotnet@v6`, and `actions/upload-artifact@v7`.

## Updating the version later

For a future release, update the `MOD_VERSION` value in `.github/workflows/build.yml`, the plugin version in `src/VStack/Plugin.cs`, and the changelog together. The manifest version and ZIP filename are generated from `MOD_VERSION`.
