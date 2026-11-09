# Seat interaction probe

The normal sample selects only seats tagged **keel-seat-probe** and uses its configured foot and exit offsets. See [Seat interaction hooks](../../docs/KEEL-SEAT-INTERACTIONS.md) for manual acceptance.

For the dismount fixture, use the verified isolated connector lab and enter a fresh managed solo creative world with no active command. Enable:

- `SEVO_KEEL_DISMOUNT_FIXTURE=1`.
- `SEVO_KEEL_DISMOUNT_FIXTURE_GAME` set to the exact isolated game path.
- `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1` and `SEVO_KEEL_EXPERIMENTAL_DISMOUNT_POSES=1`.
- The managed `SEVO_PLATFORM_SESSION` and `SEVO_PLATFORM_STATE_DIR`.

The fixture creates two stock seats on separate disposable grids. It moves only the local test character to the approach point for each seat, then uses the game's stock enter and exit flow and lets the native dismount animation finish. It first offers a clear candidate near the stock endpoint, then builds a stock hull block at that candidate and repeats. The third exit uses the unrelated seat with no candidate. The fixture leaves both test grids and the hull in the disposable world.

Read BepInEx records for `KEEL_DISMOUNT|<session>|`. Require `armed`, `clear-applied`, `native-obstruction`, `blocked-stock`, `unrelated-stock`, and `pass` in order. A clear candidate must match the final native endpoint within 0.002 m. The blocked and unrelated trials must retain the stock endpoint to the same tolerance. The blocked trial also requires measured capsule penetration at the candidate and clearance at the stock endpoint. The fixture reads the proposed and final native positions without replacing the stock exit call.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `78fdf391e5a14c6fa3f3b5a31dabbc53`. The clear exit moved 0.900 m from the stock endpoint and landed exactly at the nominated position. The stock hull produced 0.4528 m of candidate capsule penetration. Both fallback exits matched the stock endpoint. This run exercised dismount placement. It did not measure seated foot placement.

## Seated foot and yoke fixture

Run this independently of the dismount fixture in the same isolated connector lab. Use a fresh Creative solo character and leave the controls untouched. Set `SEVO_KEEL_SEATED_FOOT_FIXTURE=1`, `SEVO_KEEL_SEATED_FOOT_FIXTURE_GAME` to the exact isolated game path, and `SEVO_KEEL_SEATED_FOOT_FIXTURE_CHARACTER` to the exact disposable character name ending in a dash and the first eight session characters. Enable `SEVO_KEEL_EXPERIMENTAL_SEATED_POSE_TARGETS=1` and the managed experimental profile. The solo Mirror server must run in `dontListen` mode.

The fixture loads one transient bundled Hotwing at a clear site and enters its linked stock pilot seat and yoke. It enables third person, waits for the stock body interpolation, then requests two fixed seat-local foot positions. It observes the stock yoke hand-target path and captures native Animator goals around Keel's foot callback. It verifies both foot goals and their weights, preserves both native hand goals and their weights and rotations, then exits and verifies that both foot weights clear. It restores the prior camera perspective and leaves the transient ship in the disposable world. If the goals are out of reach, `ik-wait` records world targets, live weights, and distances from the ankle bones.

Require `armed`, `ready`, `site`, `spawned`, `ship-ready`, `seated`, `applied`, `cleared`, and `pass` in the `KEEL_SEATED_FOOT|<session>|` records. While waiting, `readiness` records show the active gate at most once every five seconds. The fixture measures targets inside the rendered Animator callback. A pass establishes the requested goals and their weights, including coexistence with stock yoke hand IK. Visual fit to actual pedal geometry, foot clipping, and the final skin deformation require separate visual acceptance.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `be8dd18a484d403da24c690568c3c4a5`. Both native foot goals matched the requested seat-local positions with zero measured error and unit position weights. The stock yoke hand goals and their weights remained intact. Both foot weights cleared after the native exit. This measures the Animator goals. Mesh fit and visual clipping depend on the mod's pedal geometry.
