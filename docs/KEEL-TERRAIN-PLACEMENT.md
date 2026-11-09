# Keel terrain material placement

Keel 0.1.10 provides a material substitution rule for freshly generated terrain. An independent mod can turn a stock surface into ore on a selected planet. Another mod can make rock strata use a different stock surface on asteroids. Both declare source and destination material references through the same rule API.

Use a disposable solo world in a managed profile. Enable `[Experimental] EnableTerrainPlacement` or set `SEVO_KEEL_EXPERIMENTAL_TERRAIN_PLACEMENT=1`. Check `TerrainPlacement.IsAvailable` for the admitted native hook.

```csharp
TerrainPlacement.Register(ownerId, localName, TerrainPlacementScope.Planets,
    TerrainMaterialReference.Stock(sourceShaderName),
    TerrainMaterialReference.Stock(destinationShaderName),
    seed: targetPlanetSeed);
```

Declare rules from the dependent plugin's `Awake`. Use exact names from the loaded stock terrain asset's `ShaderName` fields. The material resolver requires an unambiguous native row and preserves the native table when a name cannot be resolved. `TerrainPlacementScope.Asteroids` uses the instance seed. `TerrainPlacementScope.All` selects both. Omit `seed` to target all bodies in that scope. A matching planet seed is the body's stock seed. A matching asteroid seed is the stock instance seed, including for a baked ring template.

The adapter resolves rules to a byte lookup table and applies it to both original material channels once per eligible buffer. Rules do not chain. Competing rules on the same source retain its stock value, while unrelated source rows can still change. Density, positions, voxel color, and blend weights retain their native values.

Use `TerrainMaterialReference.Registered("owner:local-name")` for a destination published by Keel's terrain material registry. Keel resolves it only from the currently admitted, owned native material table. The registry's world-entry checks and persistent row backing protect stored IDs. A missing registered destination leaves that rule unapplied. Independent stock destinations can still resolve against the stock table.

Asynchronous generation applies the mapping only to successful GPU readbacks in the native valid-node branch before copying into the octree. Synchronous asteroid baking applies the same mapping after stock GPU readback and before stock mesh jobs. The mapping has no LOD-dependent input. Shader material sampling can still vary across levels. Saved chunks enter the stock save-load path and retain their stored materials, so enable the intended rule set before generating terrain and keep the same set when reopening its world.

The hook selects by existing stock material, scope, and optional seed. Depth rules and spatial voxel placement require a validated reference frame and separate LOD checks.

Build [Example.TerrainPlacement](../samples/Example.TerrainPlacement/Example.TerrainPlacement.csproj) and set `SEVO_KEEL_TERRAIN_PLACEMENT_PROBE=1` in a disposable lab to compare the stock readback with the native octree or baked mesh input. The sample normally targets stock raw iron. Set `SEVO_KEEL_TERRAIN_PLACEMENT_CUSTOM=1` alongside the terrain material registration sample to use its registered iron row. The probe reports encountered native materials and requires at least one matching solid sample. Override the lab source list with `SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_SOURCES` using semicolon-separated stock shader names.
