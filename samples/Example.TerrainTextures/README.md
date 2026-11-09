# Terrain texture probe

Build this sample against Keel 0.2.0 and Starship EVO Steam build 26451593 / 26w45a. Copy only the sample DLL into an isolated test installation. Enable `SEVO_KEEL_TERRAIN_TEXTURE_PROBE=1` and `SEVO_KEEL_EXPERIMENTAL_TERRAIN_TEXTURE_OVERRIDES=1` in a managed disposable profile. The sample uses stock donor layers as fixture inputs and supplies them through the public API.

The probe checks the two shader bindings, an independent diffuse override for sand and normal override for raw iron, the native GPU material rows and diffuse mip colors, and exact preservation of every stock layer. It samples the GPU donor and replacement layers at mips 0 and 4 through an uncompressed render target. After returning to the title and opening the profile again, expect another `KEEL_TERRAIN_TEXTURE_PROBE PASS` with an incremented cycle number.

For a separate planet bake comparison, set `SEVO_KEEL_TERRAIN_TEXTURE_DISTANT_CAPTURE=stock` in a stock lab run without the override or probe flags. Use `override` with both flags enabled for the second run. Start the same seeded solo system. Compare the six face hashes in `KEEL_TERRAIN_TEXTURE_DISTANT` by planet, face and level. This reads native baked texture pixels. An in-world visual inspection of planetary and asteroid draw calls is separate.
