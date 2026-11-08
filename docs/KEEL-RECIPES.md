# Native recipes

Keel 0.1.6 provides experimental additive recipe registration for player inventory crafting, the crafting bench, and the assembler. Enable `[Experimental] EnableRecipeRegistration` or launch a managed Uplink test session with `SEVO_KEEL_EXPERIMENTAL_RECIPES=1`.

Register a namespaced key and positive revision during the dependent plugin's `Awake`. Keel invokes the factory on the game thread after native recipe and item resources initialize. The factory can use live registered brick IDs or existing stock recipe elements.

```csharp
RecipeRegistration.Register("example.recipes", "alternate_slab", 1, () =>
{
    var stock = TabSchematics.STONE_SLAB;
    var input = stock.element0;
    input.quantity = 3f;
    var output = stock.output;
    output.quantity = 2f;
    return new RecipeDefinition(CraftingLevel.craftingBench, 7.5f, output, input);
});
```

A recipe needs one to five positive input quantities, a positive whole output count, and a finite positive duration. Combine overlapping ingredients into one quantity. Use live registered bricks or known native schematic item identities. Ingredients can retain a context and flags. Native crafting requires the output to use the default context and flags. The factory supplies a description and should leave the stock tables alone. A rejected factory reports its key and reason in the loader log.

After publication, resolve the live numeric ID with `RecipeRegistration.TryGetSchematicId("example.recipes:alternate_slab", out var id)`. The lookup is unavailable during native resource reconstruction.

Keel assigns a free ID after the live stock range, preserves the ID across managed profile restarts, and retains removed IDs in the profile as reservations. Native assembler, player and bench consumers use the resulting game schematic table. Keel publishes after stock scrap calculation to keep alternate recipes from changing vanilla recycling costs. The stock research lookup treats a new schematic with no research association as available.

The stock furnace and crucible use explicit candidate IDs and conveyor pull requests inside a shared Burst job. They need their own native integration. The recipe API currently accepts player, bench and assembler levels.
