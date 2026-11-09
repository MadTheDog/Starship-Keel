# World surface queries and resident terrain editing

Both capabilities are experimental and require a managed solo profile on Keel 0.1.10. Enable `EnableWorldSurfaceQuery` in the `Experimental` config section, or set `SEVO_KEEL_EXPERIMENTAL_WORLD_SURFACE_QUERY=1`. Enable edits separately with `EnableTerrainEdits` or `SEVO_KEEL_EXPERIMENTAL_TERRAIN_EDITS=1`. The profile capability reports are `worldSurfaceQuery` and `terrainEdits`.

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

The preview accepts a radius up to four metres, at most eight resident nodes, and one terrain-hit request per rendered frame. The selected node and any affected native saveable nodes must already have loaded density and geometry. A boundary touching an unavailable node or leaving the current octree root yields `TerrainNotReady` before a write. A stock planet terrain hit is required. Fully empty chunks and asteroid edits are outside this preview.

`Queued` means density was published and native geometry rebuild and persistence work were scheduled. Stock processing decides when these operations finish. The service stages original samples and prepares the native geometry and save queues for every changed node before writing densities. If a copy fails, it restores all attempted nodes in reverse order before the stock queues consume live density. The service disables itself after a native failure and identifies any nodes whose restoration also failed. Foliage refresh follows a successful density commit. Stock persistence writes each node asynchronously, so process exits and disk failures can still leave incomplete saves. Use a disposable profile for acceptance.

## Verification

Build the [read-only probe](../samples/Example.WorldSurfaceProbe/Example.WorldSurfaceProbe.csproj) and place it in a dedicated test installation. Aim the camera at a grid, dry ground, and open water, then place terrain or a grid in front of the water to check native occlusion. The probe logs observed types and distances and performs no edits.

For edit acceptance, call the edit API from a test mod only after an explicit test action. Inspect a cut and a fill at the same patch, exercise a chunk boundary, verify collision after the native rebuild, and reload the world to compare the terrain. Check that unloaded chunks and incompatible modes refuse the request. Static Cecil tests verify required signatures, stock water routing, the save call relationship, enum compatibility, identity drift, and the cut/fill density math. These do not establish game runtime results.
