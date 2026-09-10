# Build and publish VStack

## Recommended GitHub flow

1. Create a **public** GitHub repository named `VStack`.
2. Upload the contents of this repository ZIP to the repository root.
3. Open the repository's **Actions** tab and run `Build VStack`, or push to `main`/`master`.
4. Download the Actions artifact named `VStack-Thunderstore-v1.0.0`.
5. Upload the generated `VStack_Thunderstore_v1.0.0.zip` to Thunderstore.

The workflow replaces `__REPOSITORY_URL__` in `manifest.json` with the real GitHub repository URL and writes the exact Git commit hash into `Source/BUILD_COMMIT.txt`.

## Runtime vs source

BepInEx loads the compiled managed `VStack.dll`; `.cs` files are source code and are not loaded directly by BepInEx.

The Thunderstore package therefore contains:

- `BepInEx/plugins/VStack/VStack.dll` for users.
- `Source/Plugin.cs` and `Source/VStack.csproj` for review.
- `Source/BUILD_COMMIT.txt` to identify exactly which Git commit produced the package.

There are no custom native DLLs in VStack.
