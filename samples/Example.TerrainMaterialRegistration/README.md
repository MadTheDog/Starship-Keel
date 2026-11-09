# Terrain material registration probe

Build the sample against Keel 0.1.10 and Starship EVO Steam build 26451593 / 26w45a. Copy only the sample DLL into an isolated test installation. Set `SEVO_KEEL_TERRAIN_MATERIAL_PROBE=1` and `SEVO_KEEL_EXPERIMENTAL_TERRAIN_MATERIAL_REGISTRATION=1` in a managed disposable profile.

The sample registers independent cliff and raw-iron backings. It checks their assigned IDs, the persistent registry, the global terrain material count, and the copied native material rows. Look for `KEEL_TERRAIN_MATERIAL_PROBE PASS`.

To exercise a removed declaration, restart with the same profile marker and registry, plus `SEVO_KEEL_TERRAIN_MATERIAL_PROBE_TOMBSTONE=1`. The ridge ID must remain reserved with a stock rock backing. The iron ID must retain its original assignment. The probe does not write terrain chunks.
