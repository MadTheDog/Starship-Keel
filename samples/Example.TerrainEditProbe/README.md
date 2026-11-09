# Live terrain edit acceptance probe

The probe exercises Keel's resident terrain edit service in a rendered disposable solo world. It captures the actual packed native node samples immediately before and after each requested write, then uses later requests to check rebuilt collision and persistence. A managed test session, explicit probe opt-in, a current request file, and a stock Switch activation are required.

## Prepare the fixture

1. Build `Example.TerrainEditProbe.csproj` and stage its DLL in the dedicated Keel 0.1.10 lab. Keep the lab's verified game build, dedicated LocalLow identity, and content registry together. Enable experimental functional blocks, `EnableWorldSurfaceQuery`, and `EnableTerrainEdits`. Set `SEVO_KEEL_TERRAIN_EDIT_PROBE=1` in the lab launch environment.
2. In a disposable solo world, build a stationary fixture over a loaded, gently sloped or level patch of dry planet terrain. Use terrain with visible geometry and room around the target. Place the **Probe Terrain Edit Anchor** 5–10 metres above it with the anchor's up direction pointing away from the surface. Link a stock Switch directly to the anchor while the Switch is off. The probe rays down from the anchor, ignoring its own grid.
3. Keep the fixture and player near the same patch for the whole sequence. Use a baseline snapshot to confirm that the ray hits resident terrain. Prefer the finest loaded voxel size and the same node on consecutive snapshots. Make a stopped backup of this isolated world and its Keel registry before the first mutation.

## Submit requests

Use the current verified lab run's `SEVO_PLATFORM_STATE_DIR` and `SEVO_PLATFORM_SESSION`. Every request uses a fresh 32-digit GUID and one operation. Set the Switch off before writing the request and flip it on once afterward.

```powershell
# Set these to the verified running lab's state directory and session values.
$state = '<lab session state directory>'
$session = '<lab session id>'
$requestId = [guid]::NewGuid().ToString('N')
$operation = 'snapshot' # Use snapshot, cut, or fill.
"$session $requestId $operation" | Set-Content -LiteralPath (Join-Path $state 'terrain-edit-probe.request') -Encoding Ascii
```

A request is consumed once per game process. Use a new ID for each retry. The probe works in the stock post-transform callback on the game thread and uses the public edit service for writes. Cut and fill use a three-metre radius and move the plane by 0.75 metres down or up from the current hit.

## Verify evidence

1. **Baseline.** Record the `phase=snapshot` line with `tree`, `voxel`, `rayDistance`, `solid`, `sha256`, and the native save timestamp or absence. Keep the named payload in the run's `terrain-probe` directory.
2. **Cut.** Submit `cut`. Require the same tree for the `before` and `after` payloads, different hashes, and `result=Queued accepted=True` with `removedSolid>0` and `addedSolid=0`. The immediate `after` ray distance is the original pre-edit hit. Wait for native rebuild and send a new `snapshot`. The rebuilt ray should hit deeper from the fixed anchor. Check the cut surface visually and walk or land on it to exercise normal collision.
3. **Fill.** Submit `fill` over the same patch. Require `result=Queued accepted=True` with `addedSolid>0` and `removedSolid=0`. Use a later `snapshot` to check the ray hits nearer the anchor and inspect collision on the new surface.
4. **Persistence.** Save normally and exit the lab. Keep the same disposable profile and registry, then relaunch under a new managed run session. Approach the fixture at the same range and send a fresh `snapshot` request. Compare the same tree's packed hash, ray distance, solid count, and native save filename to the last post-rebuild snapshot. A different tree or voxel size makes the comparison inconclusive. Record the save file's observed write timestamp.
5. **Baseline restoration.** With the lab stopped, open the pre-edit backup as a separate disposable profile with the matching registry. Submit a baseline `snapshot` from the restored anchor and compare its node hash with the original baseline. Keep the edited profile and its evidence available for comparison.

Each payload is a 64,000-byte little-endian capture of the native 20×20×20 sample lattice. Each sample contains 32-bit color, signed 16-bit density, and the two texture ID bytes in native index order. Use `Get-FileHash -Algorithm SHA256` on the payload to reproduce the log's hash. Negative density denotes solid material. A cut must increase density monotonically and convert at least one solid sample to air. A fill must decrease it monotonically and convert at least one air sample to solid.

`TerrainNotReady` indicates a rejected fixture patch. Use a different loaded interior patch or wait for the native terrain jobs, then issue a new request. A missing payload or `accepted=False` is a failed acceptance check. Preserve the BepInEx log, the run configuration, and raw payloads for every result. The headless evidence tests validate request isolation and signed-density comparison. Rendered cut, fill, and persistence remain separate checks.

## Optional automated fixture

Use a fresh disposable solo Creative world in the starter system. A separate lab driver can create that world through the stock character UI. Set the following in the verified lab launch environment in addition to the managed profile, probe opt-in, and runtime feature settings above.

| Variable | Value |
| --- | --- |
| SEVO_KEEL_TERRAIN_EDIT_FIXTURE | 1 |
| SEVO_KEEL_TERRAIN_EDIT_FIXTURE_GAME | Exact isolated BepInEx game root |
| SEVO_KEEL_TERRAIN_EDIT_FIXTURE_COMPANY | Exact isolated Unity company name |
| SEVO_KEEL_TERRAIN_EDIT_FIXTURE_PRODUCT | Exact isolated Unity product name |

The fixture waits for the Creative starter medbay to finish spawning the local player in a non-listening solo host. It follows the stock Survival introduction's native relocation to the existing starter planet, then scans for a dry resident patch and moves the player above it to stream the local detail. It uses a fixed system-space measurement ray across baseline, cut, and fill. It stops after the rebuilt fill or a failure. It requires no block or request file.

A successful run reports **KEEL_TERRAIN_FIXTURE** with **accepted=True** and **detail=cut_and_fill_native_geometry_rebuilt**. The five **KEEL_TERRAIN_EDIT** evidence phases are **auto-baseline**, **auto-cut-write**, **auto-cut-rebuilt**, **auto-fill-write**, and **auto-fill-rebuilt**. The rebuilt collision ray must move by more than 0.1 metres in the expected direction while preserving the matching density hash and tree identity. Disk reload and baseline restoration use the separate manual procedure above.
