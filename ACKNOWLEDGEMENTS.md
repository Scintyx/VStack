# Acknowledgements

VStack is implemented in original managed C# source and does not bundle third-party native hook code.

The project depends on **BepInEx 6 IL2CPP** at runtime and uses its public `INativeDetour` hook API. BepInEx is not redistributed inside this repository/package; Thunderstore installs it as a dependency.

The V Rising modding community and Ultimeit's **VRisingCapRemove / ConfigLimitRemover** project were useful public references when researching V Rising's server-setting limits and the known 4095/4096 client inventory display ceiling. No source file from that project is copied into VStack.

Reference project:
https://github.com/Ultimeit/VRisingCapRemove
