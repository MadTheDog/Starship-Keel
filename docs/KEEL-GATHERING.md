# World prop gathering

Keel 0.1.7 can add a registered item when a selected world prop breaks and emits stock loot in a managed solo profile. Enable `[Experimental] EnableItemRegistration` and `EnableGatheringDrops`, or set `SEVO_KEEL_EXPERIMENTAL_ITEMS=1` and `SEVO_KEEL_EXPERIMENTAL_GATHERING=1` for a managed launcher session.

Register the item and drop rule from the dependent plugin's `Awake`.

```csharp
const string owner = "example.resources";
ItemRegistration.Register(owner, "quartz-dust", 1, () =>
    new ItemDefinition(WorldPropCommon.WorldPropId.component_construction,
        "Quartz Dust", "Dust collected from a quartz crystal."));
GatheringRegistration.RegisterDrop(owner, "quartz-dust-drop",
    WorldItemType.crystal, (int)WorldPropCommon.WorldPropId.crystal_quartz_01,
    owner + ":quartz-dust", 2);
```

Use the exact source item type and prefab ID. Each matching rule emits one stack with the declared quantity after the first successful native drop. The extra item uses native world physics and pickup and expires on the same two-minute schedule as stock prop loot. Keel resolves the output key against the current item catalog and skips unavailable outputs with a loader warning. The dispatch queue is limited to 256 stacks and drops entries that miss the following frame.

This hook applies to the managed world prop break path. Terrain voxel mining, pickups, Crusher recipes and Furnace recipes use separate paths. The rule adds an item alongside the original drop and leaves the source prefab's native loot configuration alone.

## Disposable lab probe

Build `samples/Example.GatheringProbe/Example.GatheringProbe.csproj` and install only `Sevo.Example.GatheringProbe.dll` in the isolated Keel hooks lab's plugin directory. Enable the two experimental flags above and set `SEVO_KEEL_GATHERING_PROBE=1`. The probe requires a managed session and the lab's application identity. Enter a disposable solo world.

The probe creates a stock prop, calls its native damage entry point twice and inspects live item records. It checks that the nonlethal hit emits no loot and that the lethal hit removes the source, preserves stock loot and adds one pickable quantity-two stack. It removes the synthetic drops afterward. Search the loader log for `KEEL_GATHERING_PROBE PASS` or `KEEL_GATHERING_PROBE FAIL`.
