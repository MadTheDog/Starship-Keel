# Hover rail guide proof

This disposable sample uses stock-backed marker blocks to define a short guideway. The probe supplies geometry and spring-damping policy through [Keel's grid physics hooks](../../docs/KEEL-GRID-PHYSICS.md) and checks obstruction through the [world surface query](../../docs/KEEL-WORLD-SURFACES.md). The pod remains a separate vehicle under manual control.

## Build a fixture

Enable managed experimental functional blocks, `EnableGridPhysics`, and `EnableWorldSurfaceQuery`. Build and install the sample DLL in an isolated Keel 0.1.10 profile.

1. Use a stationary grid and place unscaled **Probe Rail Straight** and **Probe Rail Right Turn** markers with matching grid units and up direction. Each segment begins at its marker. The straight extends four units along its local forward axis. The right turn begins tangent to its forward axis and exits four units forward and four units right, facing right. The left turn uses the opposite lateral direction.
2. Place the turn marker four units ahead of the first straight with the same orientation. To add an exit straight, place it four units ahead and four units right of the turn marker, then orient its forward axis toward the turn's right side. Leave clear space along the path between markers and about 1.5 units above it.
3. On a separate maneuverable pod, place a **Probe Guide Pod** marker and link a stock Switch to it. Approach the straight slowly with the pod marker within 1.2 units of the guide centre at ride height. Turn the Switch on and look for `KEEL_HOVER_RAIL captured`.
4. Move manually forward and guide through the turn at low speed. The sample can hand off at a shared endpoint with matched tangents. Look for `KEEL_HOVER_RAIL continuous segment handoff`. Reverse through the same join to check both directions.
5. Turn the Switch off, move out of range, remove a captured marker, and obstruct the guide corridor in separate tests. Each should release the pod. Toggle off and back on to arm a new capture after loss.

The guidance applies force perpendicular to the local tangent at the guide pod marker. Native propulsion and steering provide along-track movement and orientation. Each guide pod controls one point. More than one guide point and different gain settings can change stability, so verify them separately from the initial single-pod fixture.

The geometry test checks segment bounds, curve projection, and matching straight-to-curve tangents. On Steam build 26451593 / 26w45a, rendered disposable solo fixtures captured a separate pod, crossed a straight join or followed a right-hand bend past the probe's travel threshold, and released on the stock Switch. The lab used diagnostic completion for stalled stock render-end jobs and held the stock Creative tutorial deferral while driving the fixtures. Obstruction recovery, heavy pods, and save/reload still require separate checks.

For the automated straight test, boot a fresh managed Creative solo world in a dedicated game copy. Set `SEVO_KEEL_HOVER_RAIL_FIXTURE=1` and set the corresponding `_GAME`, `_COMPANY`, and `_PRODUCT` variables to that copy's exact game path and Unity identity. The fixture builds its own markers, moves the pod using the physics hook, and reports `KEEL_HOVER_RAIL PASS` after native Switch release.

Set `SEVO_KEEL_HOVER_RAIL_FIXTURE_CURVE=1` to exercise the experimental straight-to-right-turn setup. The probe checks that the separate pod travels into the bend before release. Point-velocity sampling uses native floating-origin world coordinates for both grids.
