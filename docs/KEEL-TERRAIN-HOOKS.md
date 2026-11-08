# Keel terrain and scanner hooks

Starship Keel 0.1.6 adds three independent hooks. A dependent BepInEx plugin declares its own rules and subscribers during `Awake`. The runtime validates each native capability separately. Each hook requires a valid managed profile and its own experimental opt-in.

| Capability | BepInEx `[Experimental]` setting | Launch flag |
|---|---|---|
| `oreGeneration` | `EnableOreGeneration` | `SEVO_KEEL_EXPERIMENTAL_ORE_GENERATION=1` |
| `terrainMaterialVisuals` | `EnableTerrainMaterialVisuals` | `SEVO_KEEL_EXPERIMENTAL_TERRAIN_MATERIAL_VISUALS=1` |
| `oreScannerSnapshots` | `EnableOreScannerSnapshots` | `SEVO_KEEL_EXPERIMENTAL_ORE_SCANNER=1` |

## Adjust existing ore generation slots

Use `OreGeneration.Register(ownerId, localName, scope, nativeSlot, size, threshold)`. Choose `OreGenerationScope.Planets`, `Asteroids`, or `All`. Slot indexes run from zero to `OreGeneration.SlotCount - 1`. A rule can replace either or both values. Size is a positive finite native value. The threshold is a finite value from zero to one, with one representing a zero threshold contribution. These values are native generator inputs.

The game creates its stock ore payload first. In a native solo session, Keel copies it, applies declared changes, and recomputes `totalWeight` from the thresholds. Native ore offsets, material assignments, array sizes, and shader buffer layout are retained. If mods target the same slot in the same scope, Keel logs the sorted owners and keeps that slot stock. Disjoint rules still apply. Registration closes at first solo generation.

Use a new, disposable solo world and keep its rule set constant across sessions. A changed rule set can generate different terrain from the same seed. Keel checks native solo mode when applying the rules. World migration, saved terrain reconciliation, and switching modified worlds into multiplayer require separate support.

## Adjust a named native terrain material

Use `TerrainMaterialVisuals.Register(ownerId, localName, nativeMaterialName, textureScale, brightnessFactor)`. The material name matches the loaded stock `ShaderName` exactly and case-sensitively. Supply one or both positive finite values. Use the native material name from the game assets or the sample’s reported material-name list.

Keel applies the fields to the row before the stock material buffer upload. Native IDs, texture assets, mining behavior, surface types, and shader layout stay with the original row. Conflicting declarations or duplicate native names retain stock values for that material. An absent name leaves the native table untouched. Registration closes at first material publication.

## Consume scanner snapshots

Use `OreScannerSnapshots.Register(ownerId, localName, observer)` to receive an immutable managed list of `OreScanGroup` values during active scans. The group exposes the native symbol ID and packed color, a count, and center and bounds in the current floating-origin frame. Mods can use this data for their own overlays. The callback runs synchronously on the game thread after the stock disposal callback and may see the current snapshot again on a frame with no fresh results. Keep the callback quick and expire your overlay when scanning ends. Keel removes an observer that throws and logs its key.

These hooks cover existing native slots and material rows plus a scanner data feed. Expanding the six native ore slots or the 23 biome material mappings requires compatible changes to the generator and GPU layout. Native scanner sprite and label registration would require a separate renderer and asset contract.

## Probe

Build [Example.OreHooks](../samples/Example.OreHooks/Example.OreHooks.csproj) and load its DLL only in an isolated Keel 0.1.6 lab. In a solo session, the sample checks the patched native planet payload, the unchanged asteroid scope, and the weight sum. An active scan with ore groups exercises the managed scanner callback. Set `SEVO_KEEL_ORE_PROBE_SCAN=1` to request one stock pulse after the sample enters a solo world. The sample lists the loaded native material names and, when `SEVO_KEEL_ORE_PROBE_MATERIAL` is set to one exact name, checks the registered brightness in the live native material row.
