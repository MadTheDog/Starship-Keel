# Navigation markers and targets

These are separate experimental capabilities for a managed solo profile. Enable `[Experimental] EnableNavigationMarkers` or `SEVO_KEEL_EXPERIMENTAL_NAVIGATION_MARKERS=1` for marker widgets. Enable `[Experimental] EnableNavigationTargets` or `SEVO_KEEL_EXPERIMENTAL_NAVIGATION_TARGETS=1` to select stock entity targets. Check the capability status before relying on either API.

## HUD markers

Call `NavigationMarkerProvider.TryAdd` on the game thread with a unique namespaced key, display name, live grid, and a position in that grid's local coordinate frame. The offset must be finite and at most ten million units in magnitude on each axis. The returned handle owns one marker. Dispose it when the marker should disappear. Cleanup occurs on the next waypoint HUD update. The registry supports up to 64 owned markers across mods.

Markers move with their loaded grid. They use separate instances of the stock waypoint widget and can show an icon and distance with the requested color. `ReadMarkers()` returns a read-only metadata snapshot in key order, including the name and transient anchor entity ID. The name is available to consumer UIs. The stock widget renders its normal distance text. The native waypoint table and galaxy warp coordinates remain under game ownership.

A dead or unloaded anchor removes its marker. Re-register after reload once the grid is live. Names and anchor IDs are not persisted by this API.

## Selecting entity targets

Call `NavigationTargetService.TrySelectEntity(target)` with a live, tracked `ParentEntity` while the player is controlling a local ship and the target HUD is active. Keel checks the target's native tracking row and rejects hidden, stale, unloaded, or self targets. Selection goes through the game's holomap action, including normal ship and HUD state updates. `TryGetSelectedEntity` reads back the live entity target. Planet, warp, and non-entity targets use other native routes.

The [sample library](../samples/Example.NavigationProbe/NavigationExamples.cs) shows an entity-relative survey beacon and stock salvage contact selection.

## Acceptance in a disposable solo profile

1. Register a marker on a loaded moving grid, then move the grid and change the camera or floating origin. Confirm that the marker follows its local offset and displays the requested distance.
2. Dispose the marker and verify it disappears on the next HUD update. Re-register it, unload or destroy the anchor, and verify the stock HUD remains usable.
3. While piloting, select a visible tracked wreck. Verify the game's target HUD and the API's selected entity agree. Exit the ship and try again. Repeat with a hidden, unloaded, and self target and confirm the service declines those requests.
4. Verify that ordinary HUD waypoints, native holomap selection, and galaxy warp targets still behave as before with either capability enabled or disabled.

The offline audit checks widget and selection method contracts, independent failure isolation, and compatibility with unrelated MVID changes. It does not exercise in-game widget rendering or target selection.
