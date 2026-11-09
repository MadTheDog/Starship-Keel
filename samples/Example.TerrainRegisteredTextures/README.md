# Registered terrain texture probe

Use Starship EVO Steam build 26451593 / 26w45a in a disposable managed hooks lab. Enable the terrain material and texture override capabilities and set `SEVO_KEEL_REGISTERED_TERRAIN_TEXTURE_PROBE=1`. This fixture runs without the separate `Example.TerrainTextures` sample so it can check the exact number of appended layers.

The probe uses stock donor mips as reproducible assets. It registers a diffuse replacement on a custom sand-backed material, a normal replacement on its stock backing row, and one invalid replacement on an iron-backed material. It verifies the two valid requests share one atlas pair, each keeps its original channel where omitted, the invalid request keeps stock, and the custom row's layer and distant fade color reach the GPU. Look for `KEEL_REGISTERED_TERRAIN_TEXTURE_PROBE PASS`.
