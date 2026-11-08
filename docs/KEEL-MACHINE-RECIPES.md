# Native machine recipes

Keel 0.1.8 provides an optional recipe adapter for the stock Crusher and electric Arc Furnace. Enable `EnableMachineRecipes` under `[Experimental]` or set `SEVO_KEEL_EXPERIMENTAL_MACHINE_RECIPES=1` in a managed solo profile. For custom parts, also enable item registration. The furnace target is `MachineKind.Furnace`. It covers the Arc Furnace only.

Declare recipes from a dependent plugin's `Awake`. The factory runs on the game thread when the solo world starts scheduling native jobs. Resolve registered item IDs inside the factory. This example declares a Crusher recipe to reclaim three rejected ceramic parts into two reusable pellets.

```csharp
MachineRecipeRegistration.Register("example.recycling", "reclaim", 1, () =>
{
    if (!ItemRegistration.TryCreateElement("example.recycling:rejected-part", 3, out var input)
        || !ItemRegistration.TryCreateElement("example.recycling:pellet", 2, out var output))
        throw new InvalidOperationException("The recycling parts are unavailable.");
    return new MachineRecipeDefinition(MachineKind.Crusher, 1.5f, input, output);
});
```

Each recipe takes one input identity and produces one output identity, with positive whole counts. Use the default flags and additional data. Crafting parts and crystals match by type and prefab ID with a zero context. Ores, ore-bearing stone and ingots match by type and exact positive material context, which is how stock storage merges those stacks. Supply a live stock prefab ID for their representation. Keel validates both identities against the live item resources and stock schematics.

Matching declarations take priority on an idle machine. A recipe requires the full count in one stack. It works through the machine's input slot or through machine storage with automatic processing enabled. Conveyor pull and push honor the machine's respective settings and the stock network, transfer mode, category and filters. The native graph restricts pulling from another machine's storage. Use a connected storage unit as the handoff between machines. Stock collectors that expel items into the world retain their native type restrictions.

The stock job drives power, effects and drill behavior. The pending output preview can appear while the grid is stalled. Keel advances custom progress only from completed powered job ticks and verifies the live input, pending output and output headroom before stock completion. If the input is replaced or space disappears, Keel cancels without consuming the input. The machine saves the input slot and committed inventory through its native save format. A pending job starts again from zero when the world loads. Use the matching item registry and mods when reopening a world with registered items.

The adapter runs in managed solo sessions and checks game contracts rather than requiring a particular build ID. A rejected recipe appears in the loader log with its key and reason. `MachineRecipeRegistration.IsAvailable` becomes true when a recipe catalog is admitted for the current world.
