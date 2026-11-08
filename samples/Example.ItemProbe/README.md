# Keel item probe

Build this sample against the private game references with the repository-local SDK. Enable the experimental item and recipe hooks in a disposable managed Keel profile.

The plugin declares two native items using one stock template and publishes a recipe that creates the first item and a second recipe that consumes it to create the other item. Its log reports `KEEL_ITEM_TABLE PASS` after checking native names, pickup, volume, rendering metadata and stack matching. It reports `KEEL_ITEM_RECIPE PASS` after checking the native recipes and their input and output identities.

With [Example.RecipeProbe](../Example.RecipeProbe/README.md) installed, set `SEVO_KEEL_RECIPE_PROBE_CRAFT_KEY=example.keel-items:use_dust` and launch with `-CraftProbe` to exercise the custom input and output through the stock bench.

Set `SEVO_KEEL_ITEM_SMOKE=1` for the stock MessagePack serialization and item-load probe, then enter a disposable solo world. `KEEL_ITEM_LOAD PASS` means the serialized custom item retained its identity and quantity, a stock item retained its original save representation, and the stock load entry point rejected a changed item ID and revision. The sample releases the loaded item without adding it to inventory.
