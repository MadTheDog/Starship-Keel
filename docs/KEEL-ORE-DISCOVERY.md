# Custom ore discovery

Enable EnableOreDiscovery or SEVO_KEEL_EXPERIMENTAL_ORE_DISCOVERY=1 in a managed disposable solo profile. Register a namespaced provider using OreDiscoveryProvider.Register during the dependent plugin's Awake. A provider runs on the game thread once per accepted stock scanner pulse. Keel passes the system seed, system-relative origin in metres and stock scan radius. Mods own the query and resource rules.

```csharp
OreDiscoveryProvider.Register("example.survey", "deposit-scan", scan =>
    myDepositIndex.FindNearby(scan.SystemSeed, scan.OriginX, scan.OriginY,
        scan.OriginZ, scan.RangeMeters));
```

The mod supplies the deposit index and returns results relative to the scan origin.

Return a read-only list of OreDiscoveryResult values with a namespaced material key, offset from the pulse origin in metres, and a positive sample count. Keel validates distance and caps each provider at 32 published results and each pulse at 128. An exception disables only that provider for the session. Keep callback work bounded because it runs synchronously.

Read OreDiscoveryProvider.Latest on the game thread while the pulse is active. It expires on system transition or reload. Draw custom labels and icons from the owning mod. Stock scanner widgets remain on their own rendering path, and OreScannerSnapshots continues to expose stock results. Discovery does not register material rows or decide where ore is placed.
