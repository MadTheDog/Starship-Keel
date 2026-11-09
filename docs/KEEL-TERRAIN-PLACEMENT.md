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

## Planet and moon radial bands

Enable the base hook and `[Experimental] EnableTerrainRadialPlacement` or set `SEVO_KEEL_EXPERIMENTAL_TERRAIN_RADIAL_PLACEMENT=1`. Check `TerrainPlacement.AreRadialBandsAvailable` after startup. The extra audit covers native sample centres, density ordering and the body reference frame. Ordinary substitutions stay available if this audit fails.

```csharp
TerrainPlacement.RegisterPlanetRadialBand(ownerId, localName,
    TerrainMaterialReference.Stock(sourceShaderName),
    TerrainMaterialReference.Registered(destinationKey),
    new TerrainRadialBand(-300, -100), seed: targetPlanetSeed);
```

Offsets are metres from the nominal planet radius, with an inclusive minimum and an exclusive maximum. Negative offsets select inward and positive offsets outward. Surface relief and caves vary around the nominal sphere, so a band does not measure the distance to the local surface. A mod can use this contract to place deeper ore or distinguish sediment layers. Sample spacing varies with the native level of detail. Use bands wide enough for the levels at which the layer must appear.

Keel resolves up to 16 disjoint bands for each source material on a body. Overlapping bands, an unconditional substitution for the same source, or more than 16 bands leave that source at its stock value. Rules for other sources still apply. Asteroid generation uses the ordinary placement rules. A missing body or invalid coordinate leaves the radial rules unapplied.

Build [Example.TerrainPlacement](../samples/Example.TerrainPlacement/Example.TerrainPlacement.csproj) and set `SEVO_KEEL_TERRAIN_PLACEMENT_PROBE=1` in a disposable lab to compare the stock readback with the native octree or baked mesh input. The sample normally targets stock raw iron. Set `SEVO_KEEL_TERRAIN_PLACEMENT_CUSTOM=1` alongside the terrain material registration sample to use its registered iron row. The probe reports encountered native materials and requires at least one matching solid sample. Override the lab source list with `SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_SOURCES` using semicolon-separated stock shader names.

Set `SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_RADIAL=1` and enter a new solo world to compare inside- and outside-band planet samples before the stock octree copy. The probe requires remapped solid samples in two levels and unchanged solid samples outside the band.

In Keel's isolated lab, build the Fluid Probe sample as well and set `SEVO_KEEL_LAB_BOOT_SURVIVAL=1` with the runner's `-FluidProbe` switch to start the stock Survival introduction near the planet. The boot fixture skips the fluid test in this mode.

Set `SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_DIAGNOSTICS=1` for bounded dispatch, readback, and octree copy counters. If the isolated renderer stalls, the sample can submit the already recorded stock terrain command buffer. This path requires the runner's `-DiagnosticJobCompletion` switch, `SEVO_KEEL_LAB_SUBMIT_TERRAIN_BUFFER=1`, and `SEVO_KEEL_LAB_TERRAIN_GAME` set to the exact managed lab game path. Keel's runtime does not use this fallback.

On Steam build 26451593 / 26w45a, the Survival lab observed remaps to stock raw iron and, in a separate run, a registered material row. Both runs covered two native detail levels and preserved density and packed colour. The registered-material run verified 79 solid samples inside the band and 453 outside it. The lab used diagnostic buffer submission. The normal camera submission path and saved terrain reload require separate checks.
