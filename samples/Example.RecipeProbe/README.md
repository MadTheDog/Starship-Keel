# Recipe probe

This lab sample registers an alternate stone slab recipe at the crafting bench. Three small stones yield two slabs after 7.5 seconds. It checks the managed index, native job table and Codex list for the same admitted row and logs `Recipe probe passed`.

Set `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1` and `SEVO_KEEL_RECIPE_PROBE_BLOCK=1` alongside the recipe opt-in to register an example hull and its assembler recipe. The probe verifies that the stock assembler picker accepts the schematic and that the native schematic table and brick properties refer to the same IDs. Success logs `KEEL_BLOCK_RECIPE_PROBE PASS`.

Build with the Keel project-local SDK and install the resulting sample DLL beside Keel in an isolated managed lab profile. Enable `SEVO_KEEL_EXPERIMENTAL_RECIPES=1` for that session. To exercise the recipe itself, open a crafting bench in survival and use three small stones. The startup probe checks registration and table publication. In the disposable hooks lab only, set `SEVO_KEEL_RECIPE_PROBE_CRAFT=1` and enter a solo world. The exercise places a real stock bench and verifies inventory deltas through its native craft action. A successful exercise logs `KEEL_RECIPE_CRAFT PASS`.
