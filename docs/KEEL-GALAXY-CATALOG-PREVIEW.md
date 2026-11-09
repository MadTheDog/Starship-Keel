# Galaxy catalog previews

Keel's read-only catalog preview prepares the data for a candidate galaxy. It leaves the loaded galaxy and saves untouched. Mods supply all star selection, placement, and route policy.

Call the runtime API on the game thread after native resources are ready. Check `GalaxyCatalogPreview.IsAvailable` before calling. To start from the stock catalog, pass a predicate to `SelectStockSubset`. The preview always preserves the stock introduction system and retains stock routes whose endpoints both survive. To author a catalog, create a `GalaxyCatalogPlan` with a collection of `GalaxyStarSeed` and `GalaxyGateRoute` declarations. Supply the stock starting star unchanged. Each star is identified by a nonzero seed and its sector. The native generator derives its subcell, appearance, and economy from that identity.

```csharp
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

GalaxyCatalogPlan plan = GalaxyCatalogPreview.SelectStockSubset(KeepStar);
GalaxyCatalogCompiledPreview candidate = GalaxyCatalogPreview.Compile(plan);
```

The plan orders stars by sector and seed and routes by starting seed. It rejects duplicate star identities, missing introduction stars, and missing or reused gate endpoints. The fingerprint is stable for identical declarations independent of input order. It covers catalog declarations rather than the game's shader data. Compilation rejects two stars that resolve to the same native map cell and emits native-format star and route rows for tools and previews. The candidate uses the stock 128 × 4 × 128 sector bounds and currently allows at most two million stars. Run the planner during preparation or on demand, outside frame-critical callbacks.

The live world-scoped provider is separate work. The game loads stock assets before it knows which character is about to start. Galaxy UI keeps a serialized catalog reference and caches GPU rows, while navigation, factions, and the starfield use runtime resource references. Safe publication requires a pre-entry world identity binding, coherent replacement across these consumers, and tested restoration when leaving the world or switching session mode.
