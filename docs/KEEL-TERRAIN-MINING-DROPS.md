# Terrain mining drop replacements

Enable `EnableTerrainMiningDrops` in `[Experimental]` or set `SEVO_KEEL_EXPERIMENTAL_TERRAIN_MINING_DROPS=1` in a managed solo profile. Also enable and register the [terrain material](KEEL-TERRAIN-MATERIALS.md) and [item](KEEL-ITEMS.md) capabilities. Declare the association during your plugin's `Awake`.

```csharp
TerrainMiningDropRegistration.Register("example.landforms", "ridge-mining",
    "example.landforms:weathered-ridge", "example.materials:stone-fragment", quantity: 2f);
```

Keel checks the dominant native material when the stock mining code is about to spawn loot. The registered material keeps its backing row's eligibility and stock drop chance. The replacement uses the admitted item identity, native world allocation, stock disposal time, the seeded scale and rotation from the stock spawn range, and the stock mining impulse. Keel assigns the scale before native registration and component allocation. An invalid range or sample leaves the original stock spawn path intact. One successful replacement takes the place of one stock drop. A material with a backing that never drops an item will not produce new loot through this hook.

Multiple rules for the same material leave its stock loot in place, even when declared by different mods. Missing material or item keys and unavailable runtime prerequisites also keep the stock path. If replacement allocation throws, Keel disables replacement and resumes stock only after the partial item can be released. The hook leaves stock mining statistics with the stock logic.
