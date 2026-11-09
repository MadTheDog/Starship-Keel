# Galaxy catalog preview and publication

Keel's read-only catalog preview prepares the data for a candidate galaxy. It leaves the loaded galaxy and saves untouched. Mods supply all star selection, placement, and route policy.

Call the runtime API on the game thread after native resources are ready. Check `GalaxyCatalogPreview.IsAvailable` before calling. To start from the stock catalog, pass a predicate to `SelectStockSubset`. The preview always preserves the stock introduction system and retains stock routes whose endpoints both survive. To author a catalog, create a `GalaxyCatalogPlan` with a collection of `GalaxyStarSeed` and `GalaxyGateRoute` declarations. Supply the stock starting star unchanged. Each star is identified by a nonzero seed and its sector. The native generator derives its subcell, appearance, and economy from that identity.

```csharp
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

GalaxyCatalogPlan plan = GalaxyCatalogPreview.SelectStockSubset(KeepStar);
GalaxyCatalogCompiledPreview candidate = GalaxyCatalogPreview.Compile(plan);
```

The plan orders stars by sector and seed and routes by starting seed. It rejects duplicate star identities, missing introduction stars, and missing or reused gate endpoints. The fingerprint is stable for identical declarations independent of input order. It covers catalog declarations rather than the game's shader data. Compilation rejects two stars that resolve to the same native map cell and emits native-format star and route rows for tools and previews. The candidate uses the stock 128 × 4 × 128 sector bounds and currently allows at most two million stars. Run the planner during preparation or on demand, outside frame-critical callbacks.

## Publish a managed solo galaxy

Set `EnableGalaxyCatalogPublication` or `SEVO_KEEL_EXPERIMENTAL_GALAXY_CATALOG=1`. Declare one catalog from the dependent plugin's `Awake`. Keel invokes the builder before stock character entry, after loading the stock source assets. A universe-customizer mod can thin the stock distribution. An authored campaign can supply its own stars and routes through the same plan.

```csharp
GalaxyCatalogProvider.Register(ownerId, "campaign", revision, () =>
    GalaxyCatalogPreview.SelectStockSubset(KeepStar));
```

The launcher must provide a dedicated native character root that stays stable when the game reinitializes its save paths. Before first activation, keep that directory empty except for `sevo-galaxy-save-root.json`, with `schemaVersion` set to 1, the managed `profileId`, and the absolute canonical `stateDirectory` and `nativeSaveRoot`. Keep the marker, profile state, and character saves together. Keel pins the provider key, revision, catalog fingerprint, and admitted character names and seeds. It refuses stock character entry when the required catalog is missing, disabled, changed or incompatible.

Publication preserves the game assets' object identities and replaces their native catalog, sector, shader and route data in memory before the consumers cache it. Sparse catalogs can omit fortress sites when a sector runs out of stars. This keeps native faction generation bounded. The runtime leaves ordinary unmarked profiles on the stock catalog.

Use separate profiles for different catalogs and restart the game between entries to custom characters. The active catalog lasts for the process so cached map and starfield data keep the same identity. Custom catalogs are solo-only. The preview and publication APIs do not migrate existing worlds.

In the disposable lab on Steam build 26451593 / 26w45a, an authored two-star catalog and one route passed stock character creation, saving and fresh-process selection. A separate 56-star stock subset also passed creation. The native gate lookups resolved both directions and the holomap used the published GPU rows. Changed, missing and disabled catalog runs refused entry and left the native character file byte-identical. Conflicting declarations, a disabled runtime hook and a cancelled stock entry left their new save roots untouched. An unmarked test root created a character using the stock catalog and routes. In a separate run, a stock ship staged on the source side used native thrust to cross the aperture. The stock warp sequence loaded the destination system, cleared its generated gate with the same ship, and saved the destination star. This run covered one outward crossing.

The isolated stock startup showed no loaded GalaxyMap objects before character entry. After system load, both map instances shared the repository catalog and one had already cached its renderer data. A live provider must prove its publication or refusal path around the map's initialization before admitting a pinned save.

The preview never writes the profile pin or activates a catalog.

`GalaxyCatalogPreview.AuditPreEntrySource()` checks the stock entry method contracts, discovers installed galaxy object references, and compares current runtime aliases in the main menu. It accepts the audited uncompressed player asset schema with stripped type trees and rejects unresolved references, unexpected prefab overrides, and initialized map renderers. It finds the stock catalog by script and field identity. Asset object IDs in its result are diagnostic.

For a read-only source probe, set `SEVO_KEEL_GALAXY_PREVIEW_PROBE=1` and `SEVO_KEEL_GALAXY_SOURCE_PROBE=1` on the existing galaxy sample. Look for `KEEL_GALAXY_SOURCE_PROBE PASS` before entering a character. A focused framework check can also read the installed asset graph by setting `SEVO_KEEL_GALAXY_CONTRACT_TEST_ASSETS` to a verified game's data directory. Neither path publishes a catalog or changes native saves.
