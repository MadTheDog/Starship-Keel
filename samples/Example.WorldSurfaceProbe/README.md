# Read-only world surface acceptance

Stage the probe in the managed disposable connector lab. Enable SEVO_KEEL_EXPERIMENTAL_WORLD_SURFACE_QUERY and set SEVO_KEEL_WORLD_SURFACE_PROBE=1. The probe executes from the stock post-transform callback.

## Automatic dry terrain and nominal ocean

Set SEVO_KEEL_WORLD_SURFACE_PROBE_AUTOMATED=1. Use the existing terrain reload fixture to resume a disposable character already near the known dry starter-planet site. Leave the terrain editor opt-in off. This sample does not move the player, spawn blocks, or change terrain.

The probe compares Keel against stock native rays with water enabled and disabled. It checks the reported kind, distance, hit point, and available terrain normal, then verifies the nominal ocean distance from the starter planet's sphere. Water must be classified only when included and must report no normal. Success logs TERRAIN PASS, WATER_NOMINAL PASS, and accepted=True. A nominal water hit can occur far from loaded solid terrain. It establishes ray semantics independently of whether the water is rendered at that position.

## Loaded shore and occlusion

For stronger coverage, use a disposable starting-planet site close enough to load both shore and open water. Set SEVO_KEEL_WORLD_SURFACE_PROBE_REQUIRE_RESIDENT_WATER=1. The automatic scan then requires WATER_LOADED PASS, which demonstrates a water hit in front of loaded underwater terrain, and OCCLUSION PASS, which demonstrates nearer dry terrain in front of a computed ocean crossing. A dry site far from water can time out in this mode.

To use the camera manually, omit the automatic flag and aim from close range at a dry patch, the open water over a visible shallow bottom, and a rock outcrop in front of that water. Compare the wet and dry log fields for each ray. The probe logs native comparisons and classifies loaded water when the wet ray reports Water and the dry ray reaches farther to Terrain. Use the terrain draw distance and shoreline visible in the game to choose the locations.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `29ad255ef85b4543a5e49eaea9aee275`. At the saved dry site the stock and Keel terrain hit and normal agreed. The water-enabled ray also matched the stock nominal ocean hit at 24 m. Loaded underwater terrain and shoreline occlusion were outside this fixture's coverage.
