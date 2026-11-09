# Navigation markers and targets

These are separate experimental capabilities for a managed solo profile. Enable `[Experimental] EnableNavigationMarkers` or `SEVO_KEEL_EXPERIMENTAL_NAVIGATION_MARKERS=1` for marker widgets. Enable `[Experimental] EnableNavigationTargets` or `SEVO_KEEL_EXPERIMENTAL_NAVIGATION_TARGETS=1` to select stock entity targets. Check the capability status before relying on either API.

## HUD markers

Call `NavigationMarkerProvider.TryAdd` on the game thread with a unique namespaced key, display name, live grid, and a position in that grid's local coordinate frame. The offset must be finite and at most ten million units in magnitude on each axis. The returned handle owns one marker. Dispose it when the marker should disappear. Cleanup occurs on the next waypoint HUD update. The registry supports up to 64 owned markers across mods.

Markers move with their loaded grid. They use separate instances of the stock waypoint widget and can show an icon and distance with the requested color. `ReadMarkers()` returns a read-only metadata snapshot in key order, including the name and transient anchor entity ID. The name is available to consumer UIs. The stock widget renders its normal distance text. The native waypoint table and galaxy warp coordinates remain under game ownership.

A dead or unloaded anchor removes its marker. Re-register after reload once the grid is live. Names and anchor IDs are not persisted by this API.

## Selecting entity targets

Call `NavigationTargetService.TrySelectEntity(target)` with a live, tracked `ParentEntity` while the player is controlling a local ship and the target HUD is active. Keel checks the target's native tracking row and rejects hidden, stale, unloaded, or self targets. Selection goes through the game's holomap action, including normal ship and HUD state updates. `TryGetSelectedEntity` reads back the live entity target. Planet, warp, and non-entity targets use other native routes.

The [sample library](../samples/Example.NavigationProbe/NavigationExamples.cs) shows an entity-relative survey beacon and stock salvage contact selection.

## Local vehicle assistance

Enable `[Experimental] EnableVehicleControlProvider` or `SEVO_KEEL_EXPERIMENTAL_VEHICLE_CONTROL_PROVIDER=1`. `VehicleControlProvider.TryBind(ship, out var handle)` accepts one lease for the currently piloted local ship in standard drive mode. Call `handle.TrySetAxes(translation, orientation)` on the game thread with finite components from −1 through 1. Renew the request each frame. It expires after one frame and releases when the handle is disposed.

Keel applies axes only at the stock local manual input call site, when all native axes and drive keys are idle. Active native autopilot and AI use their own branches. Menus, the target menu, free camera, docking, and other drive modes prevent assistance. The lease does not take possession of a ship, change its camera, send drive keys, or keep an unloaded grid active.

## Native waypoint approach

Enable `[Experimental] EnableWaypointFlight` or `SEVO_KEEL_EXPERIMENTAL_WAYPOINT_FLIGHT=1`. `WaypointFlightService.TryBeginApproach(ship, target, releaseDistance, out var handle)` starts the native normal-flight autopilot toward a live, visible tracked entity. The local pilot must be in standard drive mode with no active autopilot or pending landing or docking request. Give a finite release distance of at least 100 game units between the tracked bounds.

The service cancels its native approach on reaching the release boundary, losing its target or pilot, or disposal. If the game starts another native approach, the Keel lease yields ownership. This boundary releases control to the stock pilot path. It does not brake the ship to a stop at the marker, plan a route around obstacles, or take remote control. The stock autopilot cancel key remains available.

## Acceptance in a disposable solo profile

1. Register a marker on a loaded moving grid, then move the grid and change the camera or floating origin. Confirm that the marker follows its local offset and displays the requested distance.
2. Dispose the marker and verify it disappears on the next HUD update. Re-register it, unload or destroy the anchor, and verify the stock HUD remains usable.
3. While piloting, select a visible tracked wreck. Verify the game's target HUD and the API's selected entity agree. Exit the ship and try again. Repeat with a hidden, unloaded, and self target and confirm the service declines those requests.
4. Verify that ordinary HUD waypoints, native holomap selection, and galaxy warp targets still behave as before with either capability enabled or disabled.
5. Bind local assist in standard flight and send modest axes. Verify that the player controls and stock autopilot take precedence, an expired request stops assisting, and exiting the seat does not retain input.
6. Start a native approach in a clear flight area with a generous release boundary. Confirm release before close approach, then repeat with target loss, explicit disposal and pilot exit. Check that an existing stock landing or docking request refuses a new lease.

The offline audits check widget and selection method contracts, independent failure isolation, and compatibility with unrelated MVID changes. Vehicle checks cover finite axes, expiry, precedence and the native approach and cancellation contracts. Rendered UI and live flight still need in-game acceptance.
