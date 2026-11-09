# Keel slider paths

Enable `[Experimental] EnableMechanismPaths` or `SEVO_KEEL_EXPERIMENTAL_MECHANISM_PATHS=1` in a managed disposable solo profile. Register the path provider from a dependent plugin's `Awake`. The runtime publishes the optional `mechanismPaths` capability independently of ordinary sliders.

A provider receives the slider placement and its native travel length in metres. Return an immutable `MechanismPath` for an opted-in stock slider. The supplied vertices use slider-local offsets in metres, begin at zero, and must add up to the native travel length. Positive X is the stock travel axis. A second segment along Z creates a 90-degree corner while the carriage keeps its original facing. Return `null` for other sliders. The mod owns rail visuals, layout, identification, and persistence.

Keel selects the route during native child creation or reconstruction. A bound route keeps its mapping through movement, stopping, and rollback. An optional `canMove` predicate can stop a bound route if the rail layout becomes invalid. Retain the same provider selection and exact path on reload. If rail parts disappear, continue to reconstruct the route and return false from `canMove` until the parts are restored. The path hook does not encode route geometry in the save or reconstruct a route from missing parts. Changing the selected path for an existing off-axis save and opening that save without its provider are outside this preview.

The game retains ownership of the child grid, native pose publication, position targets, mass-based speed, and collision rollback. Keel limits per-update travel and prevents a step from crossing a corner. Native checks test successive poses. They do not prove a continuous collision sweep. Native straight extension stacks are outside this preview. A bound slider stops when an extension stack is present, and the stock shipyard instant state operation is suppressed for bound sliders.

## In-game acceptance

Use the [corner probe](../samples/Example.MechanismPathProbe/Example.MechanismPathProbe.csproj) on a simple stock slider in an isolated world. This sample supplies the path only. It leaves stock rail visuals intact.

1. Place a slider and lightweight payload with no native extension stacks. At home, tag it `keel-path-probe`, save, and reload with the probe enabled. Confirm the capability is available.
2. Move both directions. Verify the payload follows both legs, keeps its original facing, and reaches the same endpoints repeatedly. Confirm an untagged slider behaves as stock.
3. Change the tag on the bound slider during motion. Confirm it stops in place. Restore the tag and send a new target. Verify it resumes along the original path.
4. Test obstruction on each leg, at the corner, and on a large payload. Verify native rollback returns the payload to a previous route pose and leaves stock sliders unaffected.
5. In the same isolated profile with the exact original provider and route, save off-axis and reload. Compare the carriage pose and check resumed motion. Record separately any reload admission or tag-resolution limitation.

The offline suite verifies the path geometry and the native initialization, movement, publication and rollback call contracts. It does not verify rendered movement, obstructions, or disk reconstruction.
