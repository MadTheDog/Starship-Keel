# Terrain texture probe

Build this sample against Keel 0.1.10 and Starship EVO Steam build 26451593 / 26w45a. Copy only the sample DLL into an isolated test installation. Enable `SEVO_KEEL_TERRAIN_TEXTURE_PROBE=1` and `SEVO_KEEL_EXPERIMENTAL_TERRAIN_TEXTURE_OVERRIDES=1` in a managed disposable profile. The sample uses stock donor layers as fixture inputs and supplies them through the public API.

The probe checks the two shader bindings, an independent diffuse override for sand and normal override for raw iron, the native GPU material rows and distant diffuse mip colors, and exact preservation of every stock layer. After returning to the title and opening the profile again, expect another `KEEL_TERRAIN_TEXTURE_PROBE PASS` with an incremented cycle number.

Inspect rendered terrain and asteroid surfaces separately to confirm actual shader sampling at close and distant detail levels. The automated sample checks bindings and data, with no pixel capture.
