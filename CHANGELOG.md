# Starship Keel changelog

## 0.1.10 in development

- Added opt-in rocky-body environment declarations and bounded planet and ring-asteroid generation parameters.
- Added experimental terrain texture overrides that preserve stock material IDs and atlas layers.
- Added an experimental celestial layout provider for targeted radius and orbit overrides and append-only rocky planets and moons. Native mission and point-of-interest lookups resolve the same body IDs.
- Added experimental terrain material registration with optional custom layers, deterministic material remaps, and material-to-item mining drop bindings.
- Added a bounded custom ore discovery feed for accepted stock scanner pulses.
- Added a read-only galaxy catalog planner and native-format preview for mod-supplied candidates.
- Added opt-in galaxy publication for a dedicated solo save root, with native map and gate data and a persistent catalog identity check before stock character entry.
- Added experimental native storage connectors, temporary hose links, and audited Mag-Lock and seat variants.
- Added experimental context actions, cargo views, seated pose targets, dismount poses, and solo player-owned grid recovery.
- Added experimental native surface queries, bounded terrain edits, block assemblies, shipyard requirements, and machine work-rate modifiers.
- Added experimental grid physics timing and force application and fixed-facing slider path providers.
- Added experimental HUD markers, native target selection, and Event Gate output publication for declared functional blocks.
- Released Keel logic outputs now detach their native Event Gate jobs while preserving the pulse edge bit.
- Added opt-in grid attachment and release requests through stock Mag-Lock acquisition.
- Added an opt-in read-only observer for authoritative gameplay grid destruction.
- Added experimental local vehicle assistance and bounded native waypoint approach leases, with manual input precedence.
- Added an opt-in sublight input lease for the local player's last piloted ship after stock exit completes.
- Added an opt-in local camera-follow lease for a loaded grid, with stock action suppression until the ordinary view returns.
- Detect native definitions occupying inactive managed brick IDs during admission and resource checks.
- Added a separate HoverRail guide sample and geometry checks.
- Added opt-in planet and moon material bands relative to the native nominal body radius.
- Added read-only galaxy catalog checks for installed asset references, loaded aliases, and selected save identity.
- Added an opt-in Creative solo prototype for flush placement of registered item fittings against stock cargo faces.
- Added opt-in selectable pass-through geometry for stock-backed item and fluid fittings and passive hull shapes. Native placement overlap, targeting, and conveyor routing remain available.
- Keep the expanded attachment specular atlas alive across stock world loading so native blocks and mod-authored palettes retain their specular and emission data.
- Added depth bands against a deterministic, mod-supplied surface for fresh solid planet and moon samples.
- Verified a bounded stock terrain survey reproduced its canonical density after restart and applied depth bands across two terrain detail levels.
- Verified native terrain atlas replacement at GPU mips 0 and 4. The separate starter-planet color bake retained identical coarse face textures.
- Verified that a flush cargo fitting and its bidirectional routes survived stock character reload, then transferred items after reopening.
- Verified live Arc Furnace module changes against stock progress. The opt-in module doubled the rate, removal restored the stock rate, and both batches completed with the expected output.
- Verified a clear custom seat exit, stock fallback for an obstructed exit, and unchanged exit behavior on an unrelated seat.
- Verified seated foot targets reach the native Animator alongside stock yoke hand targets and clear on exit.
- Verified native cargo opening through a fresh context action and rejection of a stale action. Verified a cornering slider stops at an obstacle and resumes after its removal.
- Verified native shipyard eligibility follows assembly presence, and a short stock waypoint approach releases at the requested boundary.
- Verified public terrain and nominal ocean queries against native ray results, including explicit water inclusion and exclusion.
- Verified native HUD marker movement and cleanup and selection of a separate tracked contact.
- Verified simultaneous camera follow and sublight input, then stock pilot re-entry, exit and exact grid cleanup.
- Verified native destination loading and fresh-process character reload reproduce the same appended planet and moon layout.
- Verified a stock ship under native thrust crossed a published galaxy gate route, cleared the generated destination gate, and saved the destination star.

### Current limits

- Galaxy publication is solo-only and requires a dedicated marked save root. Gate transit was verified in one direction.
- Surface depth uses a supplied or surveyed field. Keel does not infer a global stock height field from streamed terrain.
- Camera follow and sublight control support a loaded ship within the documented local-player bounds. Full remote possession and NPC or generated wreck ownership transfer still require verified native transitions.

## 0.1.9

- Validate Keel-owned material rows and texture mip contents so compatible atlas extensions can coexist with custom attachments.
- Preserve stock initialization after a material conflict and block stock world entry with a diagnostic naming the affected content.
- Document [custom block and texture authoring requirements](docs/KEEL-MATERIAL-COMPATIBILITY.md).
- Known limitation: with EvoCore 0.9.53 and Tractor Beam 0.3.6, the world can load but the Lua Computer can show the wrong main texture.

## 0.1.8 candidate

- Added an opt-in item representation for caller-owned meshes, tint through the stock item material, and separate box colliders. The native 3D inventory preview and dropped item use the same representation.
- Added a selectable tint-responsive native surface for registered items. Mods can give a shared mesh distinct colors in the stock inventory and world render paths.
- Added opt-in exact-item processing recipes for the stock Crusher and Arc Furnace. Recipes use native machine power, output storage, and conveyor requests.

## 0.1.7 preview

- Added opt-in registration of distinct, namespaced crafting items backed by validated stock assets. Mods can use registered items as native crafting inputs or outputs.
- Added opt-in gathering rules that award registered items when selected mineable world props break and emit stock loot.
- Documented how mods can give their blocks survival recipes through the existing recipe API. Added a native assembler picker probe for a mod-owned block.
- The item and gathering preview is for disposable solo profiles. Stock crusher, furnace, and crucible recipe routing requires separate native integration.

## 0.1.6

- Added an experimental recipe registration API for player inventory crafting, crafting benches, and assemblers. Recipes use persistent namespaced IDs and publish into the game’s managed and native tables.
- Added experimental fluid definitions and post-job tank access for mod-owned producers and consumers. Compatible stock tanks and pipes carry the assigned fluid IDs.
- Added opt-in rules for existing planet and asteroid ore slots, visual overrides for existing named terrain materials, and immutable snapshots of native ore scanner groups.
- Added capability-specific contract checks, profile ID maps for recipes and fluids, and separate sample plugins. Each capability starts disabled.
- Stock furnace and crucible recipes, new native terrain slots and scanner symbols, and new fluid behavior for existing stock machines require further game integration. This preview targets disposable solo profiles.

## [0.1.5](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.5)

- Added named native Codex category selection for managed hulls, functional blocks, receivers, and attachments. The solo lab verified visible entries in Weapons, Construction, and Interior, default-category fallback, and hidden owned repeats.
- Fixed the half-bay offset in modular receivers. Centered section meshes, previews, and renderer culling bounds now align with the native bay and socket.
- Added mod-owned solid colors and albedo textures for custom attachment visuals, with optional colored glow and per-material stock paint.
- Added independent native placement and item-preview paint defaults for accepted custom visuals. Saved bricks retain their player-applied colors.
- Added custom receiver host meshes and separately identified repeat meshes. The opt-in modular layout renders one full section at each native repeat position while preserving stock collision, resize rules, and attachment sockets.
- Modular receivers retain the saved IDs of older stock repeat records and use a renderer-only substitution for their section geometry. Rejected section meshes fall back to stock visuals under the same registered IDs.
- Verified single and stretched placements, section culling, saved paint, repeat reconstruction, and stock fallback in the solo lab on Steam build 26451593 / 26w45a.

## [0.1.4](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.4)

- Added generic receiver and addon registration for native weapon side slots on vertically stretched stock templates. Mods declare compatible addon keys and read live slot snapshots with the installed attachment's key and placement.
- Added an optional custom render mesh with per-triangle baked materials. Keel preserves the selected stock collider, placement bounds, and content key. A rejected custom visual falls back to the stock template.
- Added independent checks for native attachment placement, lifecycle, mesh layout, and material packing. A changed visual contract can fall back to the stock visual while the attachment remains registered.
- Verified native placement, custom visuals and stock fallback, in-memory reconstruction, cleanup, registry continuity, and coexistence with Hull Filler's native registration on Steam build 26451593 / 26w45a.
- Confirmed all five Tractor Beam 0.3.0 upgrade meshes loaded through the native renderer and Codex preview with stock collider bounds. Tractor Beam's live mounting and towing checks remain pending.

## [0.1.2](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.2)

- Added experimental functional-block registration using mod-selected native templates.
- Added placement, load, sustained input, and cleanup callbacks. Stock Switch and Event Gate Toggle links can drive functional blocks in solo play.
- Reused validated stock materials without advancing the game's material cursor. Keel skips unsupported templates individually.
- Verified two templates, rejection of an unsupported template, placement, held input, in-memory reconstruction, and cleanup on Steam build 26451593 / 26w45a.

## [0.1.0](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.0)

- First prerelease. Added stable namespaced content IDs, persistent assignments, managed-profile audit and readiness, and experimental stock hull registration.
- Published the SDK and runtime ZIP and a BepInEx-layout ZIP for manual installation.
