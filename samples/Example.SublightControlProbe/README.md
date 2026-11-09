# Sublight control rendered probe

This optional BepInEx sample is a manual acceptance harness for Keel's experimental sublight lease. It uses normal stock piloting and exit while observing the target's native control mode. The probe sends one short forward pulse and later supplies neutral input while the operator reenters the same ship.

## Isolated profile

Build this project with the checkout's SDK and private game references. Install the resulting `Sevo.Example.SublightControlProbe.dll` alongside the matching Keel runtime in the disposable managed lab. Enable `EnableGridPhysics` and `EnableSublightControl`, or set `SEVO_KEEL_EXPERIMENTAL_GRID_PHYSICS=1` and `SEVO_KEEL_EXPERIMENTAL_SUBLIGHT_CONTROL=1`. Confirm both capabilities are available in the session's runtime status.

The launch must provide the normal managed experimental profile state and session. Also set these fixture values:

| Variable | Value |
| --- | --- |
| `SEVO_KEEL_SUBLIGHT_PROBE` | `1` |
| `SEVO_KEEL_SUBLIGHT_PROBE_GAME` | Exact isolated game root, containing the executable |
| `SEVO_KEEL_SUBLIGHT_PROBE_COMPANY` | Exact isolated Unity company name |
| `SEVO_KEEL_SUBLIGHT_PROBE_PRODUCT` | Exact isolated Unity product name |

The probe requires a loaded Creative solo session with the lab's Mirror server in `dontListen` mode. It rejects the original game's company and product identity.

## Run

1. Supply a newly created, player-owned disposable ship with a working stock spaceship computer, native pilot controls, power and forward thrust. Verify the ship can fly using normal controls. For the recorded run use a fresh ship or restart the disposable session so the probe can observe initial piloting. Start in open space clear of terrain, docking ports, and other grids. Leave the ship unoccupied and stationary.
2. Aim at the hull within 8 m and press **F6**. Verify the log identifies the intended ship and records rejection before piloting.
3. Pilot using the stock controls, stop the ship, and exit normally. The probe records rejection during piloting and immediately after exit. Wait at least five seconds and let native exit damping finish.
4. Press **F7** from a safe position. The probe attempts one lease, verifies another acquisition is rejected, and submits 35% forward input for up to 1.25 seconds. It records a passing motion check when forward-projected velocity rises by at least 0.25 m/s. It then stops submission and verifies expiration. If acquisition is declined before the pulse, the log permits another F7 attempt.
5. Move back within reach of the native pilot controls. Press **F8**, then use the stock controls to reenter within 15 seconds. The probe submits neutral input while awaiting entry and verifies the old lease stops accepting input after the native pilot takes control. Fly briefly under ordinary input and exit normally.
6. Retain the BepInEx log and session runtime status. A completed run emits `KEEL_SUBLIGHT_CONTROL_PROBE` with a final `PASS` line, and failures log their exact stage. Restart the lab to repeat the sequence.

Manual mode uses the ship supplied by the operator. Both modes measure a short forward-velocity response and check lease arbitration. Rotation, fuel consumption, multiplayer, and reload need separate tests. The sample has been compiled against Steam build ID 26451593 / 26w45a. Rendered acceptance is pending.

## Automated Hotwing fixture

Set `SEVO_KEEL_SUBLIGHT_PROBE_AUTOMATIC=1` in addition to the launch variables above. Set `SEVO_KEEL_SUBLIGHT_PROBE_CHARACTER` to the exact disposable character name, which must end with a dash and the first eight characters of the managed session ID. Enter that fresh character in free space, unseated, and leave the stock controls untouched.

The fixture selects a clear position 250 m away and uses the stock server loader with the bundled `V27 Hotwing.sevo`. It verifies that the new grid is locally owned and transient and that the one live stock computer resolves its linked yoke and pilot seat. It uses the native server commands to enter and exit that seat, checks the lease gates during those transitions, and waits for stock exit control to finish. It submits the same bounded forward pulse as manual mode, then requests native removal when the exact fixture is unoccupied and detached.

Look for `KEEL_SUBLIGHT_CONTROL_PROBE AUTO_PASS`, or `AUTO_FAIL` with a stage and reason. Use the manual sequence to test operator re-entry and expiry.
