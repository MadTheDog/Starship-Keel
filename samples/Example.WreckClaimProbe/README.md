# Wreck ownership handoff probe

This sample exercises the stock replacement and save path for a fixture-owned ordinary NPC wreck in a disposable solo profile. The fixture accepts Creative and Survival. It remains inactive unless explicitly enabled. Keep the original Steam installation, ordinary character roots, and sibling labs out of this test.

The stage run spawns a transient, unoccupied NPC record around a stock blueprint. The local player supplies stock load authority while the native identity remains NPC. The fixture has no pilot, terrain node, docking relation, or external respawner. It submits stock defeat, writes and decodes the native body and metadata, and verifies the stock save contains zero entries for the fixture. It publishes a hashed, profile-bound snapshot before requesting native retirement. After the original grid disappears from the live grid and NPC lists, the sample loads the staged wreck as player-controlled with the same persistent ID. It calls the stock save path and checks that the saved system index contains exactly one matching entry.

## Run the two phases

Use the existing verified isolated lab, candidate runtime 0.2.0 and Steam build 26451593 / 26w45a. Build the sample and copy only `Sevo.Example.WreckClaimProbe.dll` into that lab. Verify the launcher’s game and LocalLow profile paths before starting. Use the same marked profile directory and the same disposable character in both phases.

To exercise the public hook, set `SEVO_KEEL_WRECK_CLAIM_PROBE_API=1` and `SEVO_KEEL_EXPERIMENTAL_WRECK_CLAIM=1` in every run. The sample checks rejection of a live NPC and a duplicate claim, then uses the stock character picker to check the saved replacement.

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
| `SEVO_KEEL_WRECK_CLAIM_PROBE_GAME_MODE` | `creative` or `survival`, matching the stock character mode |

1. Set `SEVO_KEEL_WRECK_CLAIM_PROBE=stage`. Start the lab and create the named disposable character through the stock menu. Wait for `KEEL_WRECK_CLAIM_PROBE ... STAGED_PASS`. Close that process normally.
2. Set `SEVO_KEEL_WRECK_CLAIM_PROBE=reload` and `SEVO_KEEL_WRECK_CLAIM_PROBE_SOURCE_SESSION` to the original stage session. Start a fresh process with a new session GUID and load the same character through the stock menu. Wait for `KEEL_WRECK_CLAIM_PROBE ... RELOAD_PASS`.
3. Keep the stage body, XML, snapshot and receipt from `wreck-probe-<source session>` together with both run logs. A failure after staging leaves the private snapshot available for diagnosis.

## Exercise an interruption

Set `SEVO_KEEL_WRECK_CLAIM_PROBE_INTERRUPT=1` for the stage run. Wait for `INTERRUPTED_PASS`, which records native source retirement with zero live and saved replacements. Close that process. In a fresh process set the probe mode to `recover` and supply the exact original stage session and game mode. Wait for `RECOVERED_PASS` before running the separate stock reload phase.

With the public API flag enabled, the interruption stops after Keel accepts the claim and before it commits. Close the disposable process as soon as the probe reports `INTERRUPTED_PASS`. The next launch requires the original pending journal and observes Keel's automatic recovery after character selection.

The recovery phase requires the same character, profile, system, mode, and staged payload. It reuses a matching live replacement and waits for a matching indexed replacement to load normally. Duplicate identity, an unreadable stock index, a nonmatching live object, or an unindexed native body causes it to stop. If the Survival starting area blocks placement, the fixture can move only its disposable character to a clear same-system orbit after the stock introduction sets the initial position. It checks stock terrain and grid clearance before placing the fixture.

## Runtime evidence

The earlier diagnostic transactions ran on Steam build 26451593 / 26w45a with the Keel 0.1.10 candidate. The reload checks verified local ownership, build permission, retained defeat state and ID, and exactly one instance in the live world and stock save index.

| Mode | Fixture SGUID | Stage session | Fresh recovery session | Fresh stock reload session |
| --- | --- | --- | --- | --- |
| Creative | `21A9EB64-EF2` | `0e545844dee14627b2284ca74bac7ed1` | Stage completed replacement before exit | `c1076300341842959479f5fad7eabf6e` |
| Survival | `6DAECC26-ADF3E` | `bd40ecd19baa478293dfe48ddd980e1a` | `d6ceae6d967f4c3484c36272f8de9448` | `43c1f8d7aa274f38af5234f1599b3ce8` |

The Survival lab moved the disposable character to a clear orbit, stopped the fixture after retirement with zero replacements, and closed the process. The second process restored the staged wreck and wrote one stock save entry. The third opened the same character through the stock picker and found that exact wreck. This exercised a deliberate interruption between retirement and replacement. Recovery during the stock body and index writes remains unverified.

The 0.2.0 public API saved Creative wreck `4F8084AD-9A81F` in session `2e0bcb1475414ece8f16edb2a9a2230c` and reloaded it in `5f56dcc9d558438c9f1a6dc35f01cdad`. It saved Survival wreck `6C2A1CD0-95A22` in `ed88b66a30f9450787164caf68557889` and reloaded it in `9aca2d99c7ae44f98a223fdbf2db2666`. Both rejected a duplicate request and retained one stock save entry.

The separate 0.2.0 Survival interruption accepted wreck `246F1A73-DF17D` with a pending journal and zero index entries in `c12f1af78c724f509956f86862f674c5`. Keel resumed the claim in `6456dbdbf5e048de934b3b3959cd481b`. Stock reload in `0c5656ac3fd441e7a546eb24bb45baa2` found exactly one live and saved wreck, with the same ID, defeat state, and local build permission.

Standalone checks cover journal publication, exact profile and character binding, repeated reads, and recovery from partial writes of Keel's own bytes. World-generated wrecks require coordinated terrain retirement and a validated metadata transition because the native ownership predicate excludes world-wreck metadata.
