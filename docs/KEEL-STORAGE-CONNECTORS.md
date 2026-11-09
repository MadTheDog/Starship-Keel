# Keel storage connector variants

In a managed disposable solo profile, enable the existing experimental brick registration and set `[Experimental] EnableStorageConnectors = true` or `SEVO_KEEL_EXPERIMENTAL_STORAGE_CONNECTORS=1`. Runtime status reports the independent `storageConnectors` capability. A dependent BepInEx plugin registers during `Awake`.

```csharp
StorageConnectorRegistration.Register("example.logistics", "short-item", 1,
    "Short Cargo Fitting", () => TabStorage.CONVEYOR_TUBE_SHORT);

StorageConnectorRegistration.Register("example.logistics", "angled-item", 1,
    "Angled Cargo Fitting", () => TabStorage.PIPE_ANGLED, ItemStorageType.tube);
```

Keel allocates a persistent brick ID from the namespaced content key. Each variant uses the selected stock pipe's materials, shape, fixed size and two-port layout. The default overload also inherits its transport role. The explicit overload accepts `ItemStorageType.tube` for items or `ItemStorageType.fuelPipe` for fluids. The role controls the native storage graph and function classification. The stock material remains visible even when the role changes.

Native construction, adjacency, transfer and teardown own the storage graph. The fitting uses the stock pipe's hull-collision behavior and appears in the native Pipes category. Keep the profile registry with its world and use a new content key if a published connector needs a different role.

Choose a stock fixed-size, passive, two-port fitting with static materials. Flush Tank Join behavior, custom ports and visuals, mixed transport, and cross-grid links require their own contracts. The stock flush placement exception pairs a Tank Join with tank geometry. Cargo units use different placement rules. Cargo endpoints retain separate inventories.

## In-game acceptance

Build and load only the [probe DLL](../samples/Example.StorageConnectorProbe/Example.StorageConnectorProbe.csproj) in an isolated candidate installation. Confirm all three probe definitions are admitted and visible. Use matching stock pipes as a control.

1. Connect two cargo containers through the short fitting and transfer items. Repeat with compatible tanks and the angled fluid fitting. Test the angled item fitting with cargo at its two ports, then try to link it to a tank to confirm the remapped fitting routes items only.
2. Remove each connector during a transfer. Verify the route stops, unrelated transfers continue, and replacement restores the route. Confirm the fittings can be selected and have the same physical collision behavior as their templates.
3. Save and restart with the same registry. Verify the numeric IDs, reconstruct the routes, and repeat transfer and removal. Compare with a control world that uses stock fittings.

Static tests exercise the native signature and call audit, including unrelated MVID drift and rejection after removing native graph teardown. Those checks establish neither in-game transfers nor save, blueprint, or multiplayer behavior.
