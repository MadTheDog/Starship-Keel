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

The stock crusher, furnace and crucible choose inputs and outputs inside native jobs. The furnace and crucible also hardcode their conveyor requests. Adding rows to the crafting table cannot extend those machines. The recipe API accepts player, bench and assembler levels.

On Steam build 26451593 / 26w45a, the Crusher recognizes ore-bearing stone and damaged bricks. Its ore path has fixed output identities and quantities. The Arc Furnace has separate fixed recipe candidates and conveyor requests. Its native initializer can stage a schematic for stock timing and power, but completion can produce output before revalidating the input. Both machines have type-specific output routing, and their save records omit active processing state. A reusable machine recipe capability needs a native boundary that admits exact item identities, reserves the required input through commit, routes exact outputs, and defines how to resume or cancel in-flight work after load.

In the 0.1.7 preview, use `ItemRegistration.TryCreateElement` in the recipe factory to consume or produce [registered items](KEEL-ITEMS.md). Supply the intermediate explicitly if its only source recipe is another Keel recipe declared in the same batch. Automatic assembler scheduling of those precursor recipes is not supported.

## Craft a mod-owned block in survival

Declare the block and its recipe from the same mod. Capture the brick ID from Keel's block factory. Read it inside the deferred recipe factory, after native block registration. Use `CraftingLevel.assembler` for an assembler-only recipe. The stock assembler also lists bench and player recipes.

```csharp
int hullId = 0;
ContentRegistration.Register("example.hulls", "hull", 1, id =>
{
    var hull = StockShape.CreateHull(id, "Example Hull");
    hullId = id;
    return hull;
});
RecipeRegistration.Register("example.hulls", "hull_recipe", 1, () =>
{
    var iron = TabSchematics.Schematic_IronIngots.output;
    iron.quantity = 1f;
    return new RecipeDefinition(CraftingLevel.assembler, 4f,
        new CraftingElement(Game.WorldItemType.brick, hullId, 1f), iron);
});
```

Enable both managed block registration and recipe registration. Use the same approach with the live brick ID of a mod-owned functional block, receiver, or attachment. Each mod chooses its ingredients, duration, and yield. A new schematic with no research association is available under the stock research lookup. Blueprint shipyard accounting uses additional native metadata and is a separate integration.
