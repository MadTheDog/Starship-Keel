# Native inventory items

Enable `[Experimental] EnableItemRegistration` in a managed Keel profile or use `SEVO_KEEL_EXPERIMENTAL_ITEMS=1` with the lab launcher. Use a disposable solo profile. Keel blocks stock lobby hosting and joining while custom items are loaded.

Declare a namespaced item during the dependent plugin's `Awake`. The factory runs on the game thread after the stock world-prop tables load and before Keel builds recipes.

```csharp
ItemRegistration.Register("example.keel-items", "dust", 1, () =>
    new ItemDefinition(WorldPropCommon.WorldPropId.component_construction,
        "Probe Dust", "A part for testing Keel item registration."));
```

Each declaration receives a distinct native crafting-part ID. A valid stock crafting part supplies the geometry, inventory preview, collider, pickup behavior and volume. Your declaration supplies the name and description. Multiple items can share a template and remain separate stacks and ingredients.

Use `TryGetPrefabId` for a numeric ID within the current resource cycle. Use `TryCreateElement` to describe an ingredient or output and `TryCreateItemData` to describe an unregistered stack for a stock operation. These game-thread lookups require a live published item and positive quantities within the stock stack limit. Registration itself does not give items to the player or generate drops. Use the recipe or gathering hooks to obtain them through gameplay.

Keep `item-registry.json` with the profile and saves. Keel preserves allocated IDs and retains removed entries as reservations. Each saved item carries its profile, key and revision. The active item hook checks this identity as individual items load. The game can catch item-load errors and continue loading other parts of a world. Use this candidate only in disposable solo worlds and keep the matching registry, mod and hook enabled for every load. Avoid opening worlds made with these items after removing the mod, changing the registry or disabling the hook.

Use the item probe for runtime evidence. Basic item registration uses the stock crafting-part category and a verified existing mesh and collider. Multiplayer synchronization and save migration need separate contracts.

## Optional item representation

The item representation preview accepts `ItemVisualDefinition` as the fourth argument to `ItemDefinition`. Enable both `EnableItemRegistration` and `EnableItemVisuals` in the `[Experimental]` section of the Keel configuration. Managed test sessions can also set `SEVO_KEEL_EXPERIMENTAL_ITEMS=1` and `SEVO_KEEL_EXPERIMENTAL_ITEM_VISUALS=1`.

```csharp
ItemRegistration.Register("example.parts", "ceramic-sleeve", 1, () =>
{
    var representation = new ItemVisualDefinition(
        loadSleeveMesh(),
        new Color32(220, 190, 140, 255),
        new Bounds(new Vector3(0f, 0.01f, 0f), new Vector3(0.14f, 0.11f, 0.14f)),
        ItemSurfaceMode.Tinted);
    return new ItemDefinition(WorldPropCommon.WorldPropId.component_construction,
        "Ceramic Sleeve", "An insulating component.", representation);
});
```

- Supply a readable Unity `Mesh` with one triangle submesh, normals, tangents and UV0. The limit is 4,096 vertices and 4,096 triangles. Bake the mesh into local metres at scale 1, with its intended pivot at the item origin. Keel calculates render bounds from the vertices. Coordinates must lie within 16 m of the origin on each axis.
- The three-argument overload uses `ItemSurfaceMode.StockMaterial`. Some stock materials ignore item tint. Select `ItemSurfaceMode.Tinted` to use the game's opaque, tint-responsive rock surface with the supplied RGB in both native tint channels. This gives a textured finish for shared shapes such as color-coded cartridge grades. The physical template still supplies mass, volume and pickup behavior. Alpha is treated as opaque.
- The dropped item, stock 3D preview and inventory slot use the same native surface. Keel checks the tint mode and its live material before admitting a tinted declaration. For an item published as a crafting recipe output, the stock icon atlas generator also uses the same representation.
- `ColliderBox` is a local center and full size in metres. Each axis must be at least 5 mm. The native drop allocates a box and rigidbody. Stock mass, inventory volume and pickup eligibility come from the template. The game computes rigidbody inertia from its render bounds.
- Keel copies mesh data during registration. The mod owns the source asset. Keep the same key and revision for art updates that preserve the item's meaning. Use the matching mod and item registry for saves.

Invalid or disabled custom representations skip those item declarations, with a warning in the loader log. Mods that want a stock presentation can declare the three-argument form deliberately. This avoids silently replacing an item's requested collision bounds with a template's bounds.
