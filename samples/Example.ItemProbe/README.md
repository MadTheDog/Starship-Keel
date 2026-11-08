# Keel item probe

Build this sample against the private game references with the repository-local SDK. Enable the experimental item and recipe hooks in a disposable managed Keel profile.

The plugin declares two native items using one stock template and publishes a recipe that creates the first item and a second recipe that consumes it to create the other item. Its log reports `KEEL_ITEM_TABLE PASS` after checking native names, pickup, volume, rendering metadata and stack matching. It reports `KEEL_ITEM_RECIPE PASS` after checking the native recipes and their input and output identities.

With [Example.RecipeProbe](../Example.RecipeProbe/README.md) installed, set `SEVO_KEEL_RECIPE_PROBE_CRAFT_KEY=example.keel-items:use_dust` and launch with `-CraftProbe` to exercise the custom input and output through the stock bench.

Set `SEVO_KEEL_ITEM_SMOKE=1` for the stock MessagePack serialization and item-load probe, then enter a disposable solo world. `KEEL_ITEM_LOAD PASS` means the serialized custom item retained its identity and quantity, a stock item retained its original save representation, and the stock load entry point rejected a changed item ID and revision. The sample releases the loaded item without adding it to inventory.

Set `SEVO_KEEL_EXPERIMENTAL_ITEM_VISUALS=1` and `SEVO_KEEL_ITEM_VISUAL_PROBE=1` as well to use a sample component mesh, a tint and a smaller independent collider. `KEEL_ITEM_VISUAL PASS` verifies the render submission in both the native inventory preview and world renderer, plus the dropped item's collider bounds, rigidbody and pickup eligibility. `KEEL_ITEM_ICON PASS` verifies visible tinted pixels in the recipe output's stock inventory icon.

Set `SEVO_KEEL_ITEM_SURFACE_PROBE=1` as well to compare the same mesh and tinted surface in the stock inventory-slot renderer with two contrasting colors. `KEEL_ITEM_SURFACE PASS` records the number of visible pixels that differ and show the expected contrast.
