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

## Scan resident registered terrain

Enable EnableTerrainDepositScanner or SEVO_KEEL_EXPERIMENTAL_TERRAIN_DEPOSIT_SCANNER=1 in a managed solo profile. Enable terrain material registration to publish the material. Use the stock scanner input. Keel accepts the pulse only after the stock cooldown gate advances the timestamp.

```csharp
TerrainDepositScanner.Register("example.quartz", "scanner", "example.quartz:quartz",
    "Qz", new Color32(190, 216, 249, 255));
```

Declare one label per registered material key during Awake. The label renderer projects native body-local sample positions through the current floating origin, follows the stock fade and expiry, and draws a non-interactive ore silhouette and high-contrast symbol capsule. Badge dimensions scale with display height. Badge colors retain the declared hue and lift dark tints for readability. The closest projected custom marks take priority when badges overlap, with up to 12 drawn at once. The managed silhouette uses no stock atlas index. The native highlight adapter keeps the stock terrain sample draw records for ore-classified backing materials. It hides inherited stock glyphs only in the widget job's private copy when the completed native record matches the coordinate, transform and gradient of an admitted custom voxel. Real stock ore and ambiguous records retain their stock path. A backing that never emits native ore records can still display the custom badge. If the highlight separation audit fails, Keel keeps the badge and falls back to the audited stock-alias exclusion path. This hook works independently of the generic OreDiscoveryProvider opt-in.

The scan reads current resident solid leaf density from the native completed-frame boundary. Fresh and saved voxels use the same path once resident. Results exclude shared padding samples and parent LODs, and group samples of each material on the same body around an anchor within 20 world metres, matching the stock base grouping distance. The closest live sample represents the group. The scanner checks that sample on completed frames and drops a label when it is mined, replaced, or evicted. A new pulse can rediscover other solid samples in that cell. A system change or reload clears all marks.

A pulse looks at the first 65,536 native node table slots, samples up to 48 nearest qualifying leaves (16³ interior samples each), and publishes at most 32 labels. Nearby terrain beyond those budgets, unloaded chunks and baked-only asteroid render meshes are outside this query. An asteroid with native resident leaf density uses the same path. Enable SEVO_KEEL_TERRAIN_DEPOSIT_SCANNER_TRACE=1 in a disposable lab to log counts and result labels after a pulse. The trace distinguishes solid scanned samples, samples meeting the stock spacing/density/material predicates, and actual tracked native highlight records. The Iron glyph count covers the stock glyph path, which can include ambiguous aliases.
