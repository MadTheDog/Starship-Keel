# Keel native grid attachments

The current integration reports `gridAttachments` unavailable. The isolated game crashed while installing a native Mag-Lock detour, so Keel skips this installer pending a validated replacement.

Enable `[Experimental] EnableGridAttachments` or `SEVO_KEEL_EXPERIMENTAL_GRID_ATTACHMENTS=1` in a managed disposable solo profile. The independent capability is `gridAttachments`. Call the service from the native-safe game thread with a live stock Mag-Lock placement. A native variant using the same stock command can use the same service.

`TryGetCandidate` reports the live grid under the mount's native acquisition ray. `TryRequestAttach` takes an explicit expected grid and verifies that the native ray still selects it. The stock command retains its mass, target type, existing attachment and recursive attachment rules. `TryRequestRelease` requests the stock unlock. Both requests return true once the native command records the operation. Poll `TryGetAttachedGrid` for the applied state after native synchronization. The service serializes its own pending requests for the same mount or candidate. Stock editing, synchronization, save data, force unlock and release retain their native paths.

A station-mounted Mag-Lock can attach an eligible mobile grid. Terrain voxels have no target grid identity, and stock Mag-Lock rejects terrain-building targets. A world anchor needs a separate contract for planetary frame identity, save/load, collision, streaming and release. A whole-grid static flag changes gravity and motion behavior and does not supply those attachment semantics.

## In-game acceptance

Build the [probe DLL](../samples/Example.GridAttachmentProbe/Example.GridAttachmentProbe.csproj) and enable both grid attachments and contextual actions in an isolated managed solo profile.

1. Aim a stock Mag-Lock at a nearby free test grid. While targeting the mount with the crosshair, use F8 to request attachment. Verify native lock effects, applied state and the target following the mount. Use F9 to release, then use normal stock activation to confirm it still works.
2. Verify rejection for a target behind another grid, an already attached grid, an overweight grid, and terrain. Configure an incorrect expected target ID and verify the probe takes no action.
3. Request attachment and release repeatedly, including two managed requests for one target before synchronization. Verify consistent stock state and no transfer of attachment to the second mount.
4. Save a locked test grid and reload. Verify the native relation and stock release. Despawn a locked grid and verify native cleanup.

The standalone contract suite checks the native acquisition, eligibility, save and release call chain. In-game synchronization, motion and save/reload require a rendered lab test.
