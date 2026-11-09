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

The registered material has its own persistent voxel ID and uses the complete stock material row as backing, including its texture layers, surface type, terrain gradients and mining behavior. This preview registers identity and stock backing. Mod-supplied texture layers for a new material, custom item drops, scanner icons, and placement need their own verified contracts. Use [texture overrides](KEEL-TERRAIN-TEXTURES.md) to replace the texture layers of an existing material.

Keel reserves native stock IDs and the top byte value. The profile's `terrain-material-registry.json` owns all assigned IDs. Keep it with the saves. Removed declarations keep their IDs as tombstones, which resolve to a stock rock row for saved voxels. They are unavailable to `TryGetId` and unavailable for new placement. Stock updates that occupy persisted mod IDs, invalid registries, or incompatible definitions require fixing the profile before world entry.

Only enter stock solo worlds in the same managed profile while testing this preview. Profile migration, importing affected worlds into other profiles, multiplayer and direct game load entry points from other plugins require separate admission and compatibility work.
