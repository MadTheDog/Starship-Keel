# Wreck ownership handoff probe

This sample exercises the stock replacement and save path for a fixture-owned ordinary NPC wreck in a disposable solo Creative profile. It remains inactive unless explicitly enabled. Keep the original Steam installation, ordinary character roots, and sibling labs out of this test.

The stage run spawns a transient, unoccupied NPC record around a stock blueprint. The local player supplies stock load authority while the native identity remains NPC. The fixture has no pilot, terrain node, docking relation, or external respawner. It submits stock defeat, writes the native body and metadata into an isolated staging directory, and decodes both before requesting native retirement. After the original grid disappears from the live grid and NPC lists, the sample loads the staged wreck as player-controlled with the same persistent ID. It calls the stock save path and checks that the saved system index contains exactly one matching entry. It leaves the wreck in the disposable world for the reload run.

## Run the two phases

Use the existing verified isolated lab, candidate runtime 0.1.10 and Steam build 26451593 / 26w45a. Build the sample and copy only `Sevo.Example.WreckClaimProbe.dll` into that lab. Verify the launcher’s game and LocalLow profile paths before starting. Use the same marked profile directory and the same disposable character in both phases.

Set these environment variables in both runs.

| Variable | Value |
| --- | --- |
| `SEVO_PLATFORM_STATE_DIR` | Absolute path to the marked disposable runtime state |
| `SEVO_PLATFORM_SESSION` | Fresh 32-character GUID without hyphens for each process |
| `SEVO_PLATFORM_EXPERIMENTAL_CONTENT` | `1` |
| `SEVO_KEEL_WRECK_CLAIM_PROBE_GAME` | Absolute path to the isolated game root |
| `SEVO_KEEL_WRECK_CLAIM_PROBE_COMPANY` | Verified isolated Unity company |
| `SEVO_KEEL_WRECK_CLAIM_PROBE_PRODUCT` | Verified isolated Unity product |
| `SEVO_KEEL_WRECK_CLAIM_PROBE_CHARACTER` | `WreckProbe-` plus the first eight characters of the original stage session |

1. Set `SEVO_KEEL_WRECK_CLAIM_PROBE=stage`. Start the lab and create the named disposable character through the stock menu. Wait for `KEEL_WRECK_CLAIM_PROBE ... STAGED_PASS`. Close that process normally.
2. Set `SEVO_KEEL_WRECK_CLAIM_PROBE=reload` and `SEVO_KEEL_WRECK_CLAIM_PROBE_SOURCE_SESSION` to the original stage session. Start a fresh process with a new session GUID and load the same character through the stock menu. Wait for `KEEL_WRECK_CLAIM_PROBE ... RELOAD_PASS`.
3. Keep the stage body, XML, and receipt from `wreck-probe-<source session>` together with both run logs. A failure after staging leaves the private snapshot available for diagnosis.

The reload checks cover local ownership, build permission, retained defeat state and ID, native live uniqueness and saved index uniqueness. On Steam build 26451593 / 26w45a, stage session 0e545844dee14627b2284ca74bac7ed1 retired the NPC and saved player-owned grid 21A9EB64-EF2. Fresh process c1076300341842959479f5fad7eabf6e opened the same character through the stock picker and verified that exact grid, its defeat state, its local build permission, and one saved and live instance. This fixture scopes the native experiment to a single test-owned source. Crash and restart recovery between retirement and replacement, protection against source respawn, preservation of rich cargo and attached systems, and a public capability still need separate work. World-generated wrecks also require coordinated terrain retirement and a validated metadata transition. The native ownership predicate treats world-wreck metadata as a separate eligibility condition.
