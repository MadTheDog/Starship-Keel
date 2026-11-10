# Ore deposits and native metal items

Keel 0.2.1 adds two experimental authoring APIs for managed solo profiles. Target for this update is Steam build 26451593 / 26w45a.

## Independent deposits

Enable terrain placement, terrain radial placement and terrain material registration. Register a terrain material first and declare a deposit from the dependent plugin's Awake.

```csharp
OreDepositRegistration.Register(owner, "vein", TerrainPlacementScope.All,
    "TEX_ROCK_A", owner + ":ore-material", spacing: 48, radius: 12, chance: 0.8);
```

Distances are in metres. Spacing sets the body-local cell size, chance sets the probability that a cell contains a deposit, and radius sets the ellipsoid's major radius. Planet-only declarations can also specify a maximum inward depth relative to the native nominal radius. Actual surface depth can vary with native topography. The same coordinate gives the same deposit field result. Native LODs can sample different coordinates and hosts. A coarse lattice can miss a vein narrower than its sample spacing.

A deposit can use a `StockOreDepositReference` supplied by the mod. Use the measured count, eligible volume, and median connected deposit volume from a fixed-resolution stock survey. Keel translates these into cell occupancy and equivalent ellipsoid volume. Measure the final output in comparable terrain because host eligibility and clipping alter the observed distribution.

Keel uses world and body seeds, the namespaced key, and body-local coordinates. It uses the same field for fresh async readbacks and baked asteroid readbacks. Both material channels must carry the same allowed stock rock host before the hook changes a solid sample. Mixed samples and stock ores retain their stock material in the deposit pass. The earliest matching namespaced key in ordinal order wins. Deposits take precedence over material substitutions on samples they claim. Saved chunks load through the native path and retain their material IDs. Keep the declarations and the profile registry with the world.

## Native metal identities

Enable native items and terrain material registration. Declare an item with a compatible stock prefab, category, registered material key and tint. Ore can use a native small rock carrier. The selected prefab supplies the stock mesh and collider.

```csharp
ItemRegistration.Register(owner, "chunk", 1, () => new ItemDefinition(
    WorldPropCommon.WorldPropId.rockSmallA, "Aluminium Chunk", "Unrefined aluminium.",
    WorldItemType.ore, owner + ":ore-material", new Color32(216, 209, 194, 255), materialSymbol: "Al"));
```

Supply the same optional compact symbol for the matching ore and ingot. It accepts one to four ASCII letters or digits and is used by the native slot label. Hover and Codex names retain the display name. Existing declarations without a symbol keep the display-name fallback.

Use `WorldItemType.ingot` with a stock ingot prefab for the bar. Each category and material pair has one identity because native contextual requests use category and material. Keel rejects duplicate claims. The registered material must be live and its context must be outside the native gradient IDs. Exact contextual requests can distinguish the custom materials. The stock named ore filter presets still cover their built-in materials. Use `TryCreateElement` to create a live identity for machine recipes and `TerrainMiningDropRegistration.Register` to link the chunk to its terrain material.

The managed profile retains prefab allocations and tombstones. Keel checks the tagged serialized identity, category and material context before native item loading. Restore the original profile registry and mod if a tagged identity cannot be admitted. An occupied saved prefab ID fails allocation and is never silently reassigned. Keel's audited load guard rejects tagged items even when item registration is disabled. Enable registration to restore the items. Removing Keel also removes the guard.

## Scanner and Codex

Register a scanner label for the namespaced terrain material from the mod's Awake.

```csharp
TerrainDepositScanner.Register(owner, "scanner", owner + ":ore-material",
    "Al", new Color32(216, 209, 194, 255));
```

Enable `EnableTerrainDepositScanner` or set `SEVO_KEEL_EXPERIMENTAL_TERRAIN_DEPOSIT_SCANNER=1` in a managed solo profile. The native scan key, range and pulse lifetime still apply. The renderer shows mod-owned labels over sampled resident terrain. See [scanner settings and sampling limits](KEEL-ORE-DISCOVERY.md).

Keel lists live item registrations automatically by their persistent key under **Codex → Bricks → Crafting**. In Creative solo, use the stock drag and drop path to put a copy on the hotbar. Enable native item registration and terrain material registration for these identities. See [native items in the Codex](KEEL-ITEM-CODEX.md).

## Fixtures and acceptance

Build and load the independent Example.Aluminium and Example.Quartz plugins together in a disposable profile. The Aluminium fixture declares a deposit, mining drop, ore and bar, and Arc Furnace recipe. Quartz declares a smaller planet deposit and a separate contextual metal pair. Their terrain materials use stock-backed textures. The Aluminium deposit's explicit sample settings are for smoke tests and require a measured Iron reference for parity acceptance.

Record matched planet and asteroid control regions at a fixed resolution. Compare connected deposits per million eligible solid samples and per-deposit voxel counts. Retain native ore sample hashes and include chunk overlap, LOD and fresh-process reload observations. For items, record native pickup and inventory, stack separation, conveyor transfer, one powered Arc Furnace batch, and fresh-process reload.
