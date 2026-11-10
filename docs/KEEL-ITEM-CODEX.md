# Native items in the Codex

Keel uses the persistent key passed to `ItemRegistration.Register` to list the live item. The item declaration is also its Codex declaration. Enable `EnableItemRegistration` or set `SEVO_KEEL_EXPERIMENTAL_ITEMS=1` in the managed profile. Ore and ingot definitions also require live registered terrain materials.

In Creative solo, open the normal Codex and select **Bricks → Crafting**. Registered ore, ingots and crafting parts join the stock Crafting family. The stock Bricks search includes the registered items. Stock categories and entries keep their native positions and behavior.

The Codex uses a detached native preview item with the registered category, prefab, material context and tint. Its stock hover and render paths show the live item name, description and preview. A click on a registered item in an active local Creative solo world uses the same frozen-source item duplication as stock Codex entries. Drop the cursor stack on an eligible hotbar slot using the normal inventory controls. Opening and hovering create preview rows only. In Survival the hook does not make the stock-hidden Bricks navigation visible and registered previews cannot start a drag.

The Crafting family is filled when Bricks opens and reuses its preview rows while the live item identities stay valid. Native item resource reset and view destruction discard the old previews. New views resolve persistent keys through the current item registry.

An independent metadata audit checks the stock Crafting family, slot action ordering, filter, native item preview and Creative drag paths. If those contracts change, Keel skips the Codex integration and logs a reason. Check `ItemCodexRegistration.IsAvailable` for hook admission. Live entries also require the relevant item, terrain material and preview resources to be ready.
