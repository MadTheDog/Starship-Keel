# Keel slider paths

Enable `[Experimental] EnableMechanismPaths` or `SEVO_KEEL_EXPERIMENTAL_MECHANISM_PATHS=1` in a managed disposable solo profile. Register the path provider from a dependent plugin's `Awake`. The runtime publishes the optional `mechanismPaths` capability independently of ordinary sliders.

A provider receives the slider placement and its native travel length in metres. Return an immutable `MechanismPath` for an opted-in stock slider. The supplied vertices use slider-local offsets in metres, begin at zero, and must add up to the native travel length. Positive X is the stock travel axis. A second segment along Z creates a 90-degree corner while the carriage keeps its original facing. Return `null` for other sliders. The mod owns rail visuals, layout, identification, and persistence.

Keel selects the route during native child creation or reconstruction. A bound route keeps its mapping through movement, stopping, and rollback. An optional `canMove` predicate can stop a bound route if the rail layout becomes invalid. Retain the same provider selection and exact path on reload. If rail parts disappear, continue to reconstruct the route and return false from `canMove` until the parts are restored. The path hook does not encode route geometry in the save or reconstruct a route from missing parts. Changing the selected path for an existing off-axis save and opening that save without its provider are outside this preview.

The game retains ownership of the child grid, native pose publication, position targets, mass-based speed, and collision rollback. Keel limits per-update travel and prevents a step from crossing a corner. The original `Register` overload stops when a bound slider has an extension stack. The stock shipyard instant state operation is suppressed for bound sliders.

## Connected rails

Enable `EnableConnectedRails` or `SEVO_KEEL_EXPERIMENTAL_CONNECTED_RAILS=1` alongside mechanism paths. `MechanismPath.FromConnectedRail` accepts straight and right-angle joins. `RegisterConnectedRail(owner, name, selectPath, canSweep)` maps the native extension travel onto those junctions. Distance starts at the negative end. Keel subtracts the path offset at the native home distance so the stock home pose stays fixed. A bound slider stops if the extension counts or grid size change. The callback receives the carriage and proposed offsets for the entire bounded movement.

Also enable `EnableStockRailTools` or `SEVO_KEEL_EXPERIMENTAL_STOCK_RAIL_TOOLS=1` to opt into the native collider queries. `TryDiscoverStockStraightRail(slider, out path)` reads the slider's two live stock extension chains on the main thread in a safe solo phase. A provider may call the equivalent method on its selection context when the links are already available. The native game creates links asynchronously on load, so discovery may fail until the links settle. Stock parts provide one straight axis. A mod must supply corner junctions.

The three-argument `RegisterConnectedRail` overload uses Keel's conservative clearance check. It queries the native brick and triangle collider index for the swept bounds of the carriage and its nested child grids. It rejects unknown candidates, docked payloads, unsupported scales, unavailable snapshots and oversized bounds. It may stop at an empty part of a payload's bounding box. Candidate data comes from the current native collider snapshot. Native collider publication, in-game obstruction tests and fresh-process off-axis reload still need live validation.

## In-game acceptance

Use the [corner probe](../samples/Example.MechanismPathProbe/Example.MechanismPathProbe.csproj) on a simple stock slider in an isolated world. This sample supplies the path only. It leaves stock rail visuals intact.

1. Place a slider and lightweight payload with no native extension stacks. At home, tag it `keel-path-probe`, save, and reload with the probe enabled. Confirm the capability is available.
2. Move both directions. Verify the payload follows both legs, keeps its original facing, and reaches the same endpoints repeatedly. Confirm an untagged slider behaves as stock.
3. Change the tag on the bound slider during motion. Confirm it stops in place. Restore the tag and send a new target. Verify it resumes along the original path.
4. Test obstruction on each leg, at the corner, and on a large payload. Verify native rollback returns the payload to a previous route pose and leaves stock sliders unaffected.
5. In the same isolated profile with the exact original provider and route, save off-axis and reload. Compare the carriage pose and check resumed motion. Record separately any reload admission or tag-resolution limitation.

The offline suite verifies path geometry and native initialization, movement, publication, and rollback call contracts. A rendered solo fixture on Steam build 26451593 / 26w45a used a stock Switch to move the carriage along both legs to local offset (1 m, 1 m), then back to its origin with fixed facing throughout. A second run verified a native collision stop on the second leg and resumed motion after removing the obstacle. The lab used diagnostic job completion. Disk reconstruction and obstruction at the corner still require separate acceptance.
