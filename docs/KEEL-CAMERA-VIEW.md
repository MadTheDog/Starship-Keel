# Local camera view lease

The experimental camera view service follows an offset relative to a loaded grid. An inspection viewer can frame a component from outside the ship. A remote handset can present its selected loaded grid while its own control system handles commands. The view lease supplies neither a remote control channel nor a simulation or streaming lease.

Enable `EnableCameraView` in the `Experimental` configuration or set `SEVO_KEEL_EXPERIMENTAL_CAMERA_VIEW=1` in the managed solo profile. Check the `cameraView` capability in the runtime status.

Call CameraViewService.TryFollow with the live grid, a grid-local position and unit rotation, and retain the returned IDisposable. The maximum distance is 250 m. The lease requires a local player in a managed solo world, with ordinary stock camera control and no pending seat or command transition. The mod owns selection, presentation and controls.

Keel gives the stock camera a proxy under the physical player's hierarchy and updates the proxy from the loaded target's pose. The target does not own the camera hierarchy. Normal player movement and physics continue. During the lease the stock local input loop releases captured item inputs before it can build, place a blueprint, interact with cargo, or pick up objects. Keel blocks the stock build-options shortcut while the lease is active.

Dispose the lease or press Escape to request restoration. Leaving the admitted view state, losing the target, or exceeding the range also requests restoration. Input remains blocked until the stock camera update reaches its pose step and the native camera anchor is back on the physical player at its neutral local pose. The lease is released at that acknowledgment. Camera transitions during loading, photo mode and vehicle view retain stock ownership.

On Steam build 26451593 / 26w45a, the rendered disposable probe verified camera following after the target moved, a retained physical player, stock action suppression, and restored ordinary input after both disposal and target defeat. A second fixture used the camera and sublight leases on the same ship, restored the native camera, re-entered the stock pilot seat, exited again and removed the test grid. The lab used diagnostic job completion and temporarily deferred the stock Creative tutorial. Manual Escape and target unspawn still require separate acceptance.
