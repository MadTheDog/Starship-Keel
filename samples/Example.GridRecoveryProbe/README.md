# Grid recovery rendered probe

The optional BepInEx probe exercises Keel recovery on one new native hull grid in a disposable Creative solo world. It does not select or alter pre-existing grids.

Build the project and install only `Sevo.Example.GridRecoveryProbe.dll` alongside the matching Keel runtime in the isolated profile. Enable `EnableGridRecovery` or set `SEVO_KEEL_EXPERIMENTAL_GRID_RECOVERY=1`. Confirm the runtime reports `gridRecovery` available.

The managed launcher must supply `SEVO_PLATFORM_SESSION`, `SEVO_PLATFORM_STATE_DIR`, and experimental content mode. Set the additional variables below before starting the lab.

| Variable | Value |
| --- | --- |
| `SEVO_KEEL_GRID_RECOVERY_PROBE` | `1` |
| `SEVO_KEEL_GRID_RECOVERY_PROBE_GAME` | Exact isolated game root |
| `SEVO_KEEL_GRID_RECOVERY_PROBE_COMPANY` | Exact isolated Unity company name |
| `SEVO_KEEL_GRID_RECOVERY_PROBE_PRODUCT` | Exact isolated Unity product name |
| `SEVO_KEEL_GRID_RECOVERY_PROBE_CHARACTER` | Exact disposable character name, ending in a dash and the first eight characters of the managed session ID |

Enter that fresh Creative solo character with the lab's Mirror server in `dontListen` mode. Leave the character still in a clear area. The probe spawns the hull 250 m away and refuses to damage it unless it is live, player-owned, unoccupied, detached and at least 150 m away.

The probe rejects recovery on a healthy grid, uses native hull damage and stock defeat, then checks that a surviving hull below the game's recovery threshold is rejected. It calls the stock hull repair method and verifies the grid remains defeated until Keel invokes native recovery. It checks the same SGUID, registered object and native entity identity before and after recovery. Finally it removes the fixture through native despawn and rejects recovery using the stale reference.

Retain the BepInEx log and session runtime status. Look for `KEEL_GRID_RECOVERY_PROBE PASS` or the exact failing stage. The fixture is compiled against Steam build ID 26451593 / 26w45a. Rendered recovery, save/reload, NPC and generated wreck cases need separate verification.
