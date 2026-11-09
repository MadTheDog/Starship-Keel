# Selectable pass-through geometry

Enable `[Experimental] EnableSelectablePassThrough` or `SEVO_KEEL_EXPERIMENTAL_SELECTABLE_PASS_THROUGH=1` with managed experimental block registration. Check the `selectablePassThrough` runtime capability. Declare blocks from the owning plugin's `Awake`.

```csharp
StorageConnectorRegistration.RegisterPassThrough(
    "example.logistics", "corner", 1, "Open cargo corner",
    () => TabStorage.PIPE_ANGLED, ItemStorageType.tube);

SelectablePassThrough.RegisterHull(
    "example.instrumentation", "sleeve", 1, "Sensor sleeve");
```

An owned definition uses the native collision-off bit for physical contacts while keeping its stock selectable geometry. Keel preserves native build overlap checks in both directions, including native pipe-to-hull allowances. Ordinary build and deletion keep their stock inventory paths. An exposed fitting can be selected with the normal voxel ray. An enclosing block can occlude it. Tool and weapon rays may still hit the geometry.

A storage fitting uses the same two ports and transport graph as a regular Keel connector. Adjacent cargo units keep separate inventories. Normal registration accepts the same fixed-size passive stock templates as the storage connector API. For a flush cargo-face fitting, enable the independent Creative solo flush placement capability and pass `passThrough: true` to the overload of `RegisterFlushItemFitting`. The flush variant opts out of native symmetry and refuses placement in the preview outside Creative solo. Normal fittings follow stock placement and inventory rules. Mods that supply a Survival fitting declare its crafting recipe separately. Keep a separate content key for a new variant and retain the registry alongside its world.

Two aligned stock cargo units can touch face to face. A rendered Creative test placed both units first on the same subgrid, measured 0 m between their faces, then submitted a build at the shared seam. The fitting connected both inventories in both directions. The stock build cursor and snap calculation produced no exact seam target in 54 sampled cargo hits. The fitting-first sequence leaves the first unit's face accessible before the second unit is added. Retrofitting two already placed units requires a way to submit the exact seam position.

The passive hull declaration supplies the same contact behavior without requiring storage. A mod can use the shape as the visible housing for a sensor or cable. Mod-specific geometry, interaction rules, and processing remain with the mod.

The native audit admits stock collision publication, contact and ordinary targeting paths, and both overlap paths independently of assembly build identity. The sample probes use a managed disposable solo lab. Collision geometry remains in the native targeting structures, so the flag does not remove their query or memory cost.

The live capsule check applied zero correction at the fitting and 0.3411 m at the solid stock cargo control. A fresh-process reload restored the fitting ID, collision-off flag, local collider, separate inventories, and bidirectional item route. Global ray selection after reload remains unverified because the probe missed both the saved fitting and adjacent stock cargo even after approaching the grid.
