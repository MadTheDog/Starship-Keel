# Keel storage connector variants

In a managed disposable solo profile, enable the existing experimental brick registration and set `[Experimental] EnableStorageConnectors = true` or `SEVO_KEEL_EXPERIMENTAL_STORAGE_CONNECTORS=1`. Runtime status reports the independent `storageConnectors` capability. A dependent BepInEx plugin registers during `Awake`.

```csharp
StorageConnectorRegistration.Register("example.logistics", "short-item", 1,
    "Short Cargo Fitting", () => TabStorage.CONVEYOR_TUBE_SHORT);
```

Keel allocates a persistent brick ID from the namespaced content key and uses the selected stock pipe's materials, shape, fixed size, transport type and two-port layout. Native construction, adjacency, transfer and teardown own the storage graph. The fitting uses the stock pipe's hull-collision behavior and appears in the native Pipes category. Keep the profile registry with its world.

Choose a stock fixed-size, passive, two-port fitting with static materials. The adapter inherits the stock medium and footprint. Flush Tank Join behavior, custom ports and visuals, mixed transport, and cross-grid links require their own contracts. Cargo endpoints retain separate inventories.

## In-game acceptance

Build and load only the [probe DLL](../samples/Example.StorageConnectorProbe/Example.StorageConnectorProbe.csproj) in an isolated candidate installation. Confirm both probe definitions are admitted and visible. Use matching stock pipes as a control.

1. Connect two cargo containers through the short fitting and transfer items. Repeat with compatible tanks and the angled fluid fitting. Confirm that a mismatched medium cannot route through either fitting.
2. Remove each connector during a transfer. Verify the route stops, unrelated transfers continue, and replacement restores the route. Confirm both fittings can be selected and have the same physical collision behavior as their templates.
3. Save and restart with the same registry. Verify the numeric IDs, reconstruct both routes, and repeat transfer and removal. Compare with a control world that uses stock fittings.

Static tests exercise the native signature and call audit, including unrelated MVID drift and rejection after removing native graph teardown. Those checks establish neither in-game transfers nor save, blueprint, or multiplayer behavior.
