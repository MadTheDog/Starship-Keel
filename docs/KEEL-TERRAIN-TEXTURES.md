# Terrain texture overrides

Keel exposes an experimental visual override for existing terrain materials. Enable `EnableTerrainTextureOverrides` in the `[Experimental]` section or set `SEVO_KEEL_EXPERIMENTAL_TERRAIN_TEXTURE_OVERRIDES=1` in a managed disposable profile.

Register from your plugin's `Awake`. Supply the exact, case-sensitive stock material `ShaderName`. The factory runs on the game thread during native terrain publication, once per resource cycle that needs a new binding. Return the textures supplied by your mod. Pass a diffuse layer, a normal layer, or both. Keel keeps the original layer for an omitted channel.

```csharp
TerrainTextureOverride.Register("example.rockpack", "cliff", "TEX_CLIFF_A",
    () => new TerrainTextureLayers(diffuse: cliffDiffuse));
```

The texture for each channel must be CPU readable and match the live atlas for width, height, graphics format (including color space), and mip count. Supply authored mip data. Keel copies the bytes and leaves ownership of the input textures with the mod.

Each accepted material keeps its native material ID, drop behavior, terrain placement, and surface type. Keel creates paired texture layers and changes the material's texture index before the stock GPU upload. Other materials sharing the original stock layers retain those layers. Keel also supplies the diffuse layer's smallest mip color to the native material row. The override applies wherever the terrain shader selects that material, including asteroids. Keel provides no planet selector for this visual hook.

The material atlas and the planet's distant color bake are separate native textures. In a rendered disposable lab on Steam build 26451593 / 26w45a, the donor and replacement atlas layers produced identical sampled GPU bytes at mips 0 and 4, and the native row contained the expected fade color. All six coarsest faces of the starter planet's separate color bake were byte-identical in the stock and override runs. The override leaves the planet's native gradient palette in place.

If multiple declarations target the same material, Keel logs their keys and keeps that material stock. A missing or ambiguous native name or an incompatible input texture also leaves that material stock. Keel requires the live terrain shader bindings to match the stock loader before publication. The capability uses the terrain arrays, separately from the block and attachment palette arrays.

`TerrainTextureOverride.IsAvailable` reports that Keel enabled the adapter for the process. Inspect the loader log for individual declaration rejections. Use [terrain material visuals](KEEL-TERRAIN-HOOKS.md) for scalar changes. This capability keeps material identity and gameplay unchanged and allocates a clone of each stock atlas while overrides are active.
