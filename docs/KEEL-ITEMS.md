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

Use the item probe for runtime evidence. The feature uses the stock crafting-part category and a verified existing mesh and collider. Custom meshes, physical properties, multiplayer synchronization and save migration need separate contracts.
