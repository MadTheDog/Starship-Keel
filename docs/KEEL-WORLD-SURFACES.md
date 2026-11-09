# World surface queries and resident terrain editing

Both capabilities are experimental and require a managed solo profile on Keel 0.2.0. Enable `EnableWorldSurfaceQuery` in the `Experimental` config section, or set `SEVO_KEEL_EXPERIMENTAL_WORLD_SURFACE_QUERY=1`. Enable edits separately with `EnableTerrainEdits` or `SEVO_KEEL_EXPERIMENTAL_TERRAIN_EDITS=1`. The profile capability reports are `worldSurfaceQuery` and `terrainEdits`.

## Query a loaded surface

```csharp
var result = WorldSurfaceQuery.Raycast(origin, direction, 40f, includeWater: true, out var hit);
if (result == WorldSurfaceQueryResult.Hit)
    UseDistance(hit.Kind, hit.Distance);
```

Call on the game thread during a native-container-safe phase in a loaded solo world. The stock `GameloopManager.OnTransformJobFinished` event is one suitable call site. The ray accepts a finite positive range up to 2,048 metres. The call normalizes the direction and can ignore a currently live parent grid. Its immutable result contains the nearest stock hit as `Grid`, `Terrain`, `Water`, or `Other`. Do not cache world points across a floating-origin shift.

The stock ocean query intersects the nominal ocean sphere. It provides distance and hit type, so Keel derives the point along the normalized ray. `HasNormal` is false for water and other unclassified hits. Use the same query for a clearance sensor or a survey overlay.

## Edit a small planar patch

```csharp
var result = TerrainEditService.ApplyPlane(origin, direction, 20f, up,
    planeOffset: 0f, radius: 3f, operation: TerrainEditOperation.CutAbove);
```

The edit casts a fresh ray against native terrain and centres a spherical brush at the hit. The plane normal is in world coordinates. `CutAbove` removes material on the normal side of the plane, and `FillBelow` fills on the other side using material sampled near the existing surface. The caller owns tool input, preview, permissions, and gameplay costs. A foundation leveller and a road grader can use the same operation.

The preview accepts a radius up to four metres, at most eight resident nodes, and one terrain-hit request per rendered frame. It checks the nearest sample in each native lattice to determine whether the brush can affect that node. The selected node and any affected native saveable nodes must already have loaded density and geometry. Reaching samples in an unavailable node or leaving the current octree root yields `TerrainNotReady` before a write. A stock planet terrain hit is required. Fully empty chunks and asteroid edits are outside this preview. Set `SEVO_KEEL_TERRAIN_EDIT_DIAGNOSTICS=1` to log the first rejected candidate node and current batch count.

`Queued` means density was published and native geometry rebuild and persistence work were scheduled. Stock processing decides when these operations finish. The service stages original samples and prepares the native geometry and save queues for every changed node before writing densities. If a copy fails, it restores all attempted nodes in reverse order before the stock queues consume live density. The service disables itself after a native failure and identifies any nodes whose restoration also failed. Foliage refresh follows a successful density commit. Stock persistence writes each node asynchronously, so process exits and disk failures can still leave incomplete saves. Use a disposable profile for acceptance.

## Verification

Build the [read-only probe](../samples/Example.WorldSurfaceProbe/Example.WorldSurfaceProbe.csproj) and place it in a dedicated test installation. Aim the camera at a grid, dry ground, and open water, then place terrain or a grid in front of the water to check native occlusion. The probe logs observed types and distances and performs no edits.

A rendered solo probe on Steam build 26451593 / 26w45a matched the native dry terrain hit and normal. It also matched the nominal ocean at 24 m when water was requested and verified the result changed when water was excluded. The test site did not exercise submerged terrain or shoreline occlusion.

The [isolated edit probe](../samples/Example.TerrainEditProbe/README.md) captured a live cut and fill on one 1 m voxel node on Steam build 26451593 / 26w45a. The cut removed 21 solid samples, moving the rebuilt surface 0.836 m farther from the probe. The fill added 19 solid samples and moved it 0.746 m closer. Each rebuilt geometry check matched the recorded native density hash. A separate run saved a disposable character with the stock save path. The game loaded the same node through its normal character picker in a fresh process, marked its density as loaded from disk, and reproduced the saved hash. The lab used diagnostic native render-end completion and terrain buffer submission. Normal camera submission, walking and landing collision, and larger or seam-crossing edits require separate acceptance.

Static Cecil tests verify required signatures, stock water routing, the save call relationship, the native lattice formula, enum compatibility, and identity drift. Offline tests also verify cut and fill density math and synchronous rollback after a failed copy.
