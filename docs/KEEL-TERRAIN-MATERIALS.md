# Persistent terrain material registration

Enable `EnableTerrainMaterialRegistration` in `[Experimental]` or set `SEVO_KEEL_EXPERIMENTAL_TERRAIN_MATERIAL_REGISTRATION=1` in a managed disposable solo profile.

Declare a stable key and revision in your plugin's `Awake`, with the exact case-sensitive `ShaderName` of an existing terrain material.

```csharp
TerrainMaterialRegistration.Register("example.landforms", "weathered-ridge", 1, "TEX_CLIFF_A");
```

Resolve the key from a callback that runs after terrain publication.

```csharp
if (TerrainMaterialRegistration.TryGetId("example.landforms:weathered-ridge", out byte materialId))
{
    // A placement API can use the resolved material identity after publication.
}
```

The registered material has its own persistent voxel ID and uses a stock material row as backing, including its surface type, terrain gradients and default mining behavior. You can supply your own texture layers by enabling the [texture override capability](KEEL-TERRAIN-TEXTURES.md) and using the five-argument overload.

```csharp
TerrainMaterialRegistration.Register("example.landforms", "weathered-ridge", 1, "TEX_CLIFF_A",
    () => new TerrainTextureLayers(diffuse: ridgeDiffuse));
```

Each replacement channel must match its native atlas for dimensions, graphics format and readable mip data. If one channel is omitted, Keel uses the original stock backing channel for that side. An unavailable texture capability or rejected texture request keeps the material's stock backing and persistent ID. The four-argument overload remains available for stock-backed materials. See the separate [mining drop hook](KEEL-TERRAIN-MINING-DROPS.md) for registered item replacements.

Keel reserves native stock IDs and the top byte value. The profile's `terrain-material-registry.json` owns all assigned IDs. Keep it with the saves. Removed declarations keep their IDs as tombstones, which resolve to a stock rock row for saved voxels. They are unavailable to `TryGetId` and unavailable for new placement. Stock updates that occupy persisted mod IDs, invalid registries, or incompatible definitions require fixing the profile before world entry.

Only enter stock solo worlds in the same managed profile while testing this preview. Profile migration, importing affected worlds into other profiles, multiplayer and direct game load entry points from other plugins require separate admission and compatibility work.
