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

## Recolor stock terrain art

Enable terrain material registration and `EnableTerrainTextureOverrides` (or `SEVO_KEEL_EXPERIMENTAL_TERRAIN_TEXTURE_OVERRIDES=1`). Register the existing persistent key and revision with separate gameplay and visual stock shader names.

```csharp
TerrainMaterialRegistration.RegisterStockVisual(owner, "terrain", 1,
    stockBackingMaterial: "RAW_IRON_A", stockVisualMaterial: "RAW_IRON_A",
    tint: new Color32(241, 226, 205, 255), brightness: 1.4f);
```

Keel appends a private diffuse and normal pair. It applies the requested RGB hue to the luminance of each stock BC3 diffuse endpoint. Brightness multiplies that luminance before clipping. The alpha stream, color indices, mip chain and stock normal bytes are retained. The tint alpha is unused. The original stock layers and the backing row's gradient, metalness, mining and scanner semantics are preserved. Packed voxel colors are not rewritten. Unsupported atlases and unresolved visual sources retain stock backing. The final rendered color still depends on native gradients and lighting.

Keel reserves native stock IDs and the top byte value. The profile's `terrain-material-registry.json` owns all assigned IDs. Keep it with the saves. Removed declarations keep their IDs as tombstones, which resolve to a stock rock row for saved voxels. They are unavailable to `TryGetId` and unavailable for new placement. Stock updates that occupy persisted mod IDs, invalid registries, or incompatible definitions require fixing the profile before world entry.

Only enter stock solo worlds in the same managed profile while testing this preview. Profile migration, importing affected worlds into other profiles, multiplayer and direct game load entry points from other plugins require separate admission and compatibility work.
