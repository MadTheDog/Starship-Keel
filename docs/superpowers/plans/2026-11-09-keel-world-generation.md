# Keel 0.1.10 world generation implementation plan

> For agentic workers: use the subagent-driven-development or executing-plans workflow and keep shared wiring with the integration owner.

**Goal.** Expose the eight world and terrain capabilities approved for 0.1.10 through independent opt-in contracts.

**Architecture.** Keep public declarations typed and namespaced. Resolve them at audited stock boundaries, with deterministic composition or stock fallback on conflicts. Keep presentation, world presets, assets and content in the dependent mod. Start in disposable managed solo profiles.

**Baseline.** Keel v0.1.9 commit 03b44cc and Starship EVO Steam build ID 26451593 / 26w45a. Validate required members and call relationships rather than using build identity for admission.

| Slice | Owner | Verification gate |
|---|---|---|
| Body environment | Body agent | Verify environment feeds native physics, ocean, and biome generation coherently. |
| Existing terrain texture layers | Textures agent | Preserve other material rows and prove both planetary terrain and asteroids. |
| Planet and asteroid generation inputs | Terrain agent | Keep texture, ore, and environment ownership separate. Verify final native payloads. |
| Celestial layout | Integration owner | Start with stable stock identity and append-only moons. Verify the mission lookup and system load see the same ordered catalog. |
| New materials and placement | Integration owner | Prove material IDs survive save/load before composing custom ore placement. Validate node boundaries and matching detail levels. |
| Scanner discovery | Integration owner | Keep stock scan results available. Bound custom results and tie discovery to an actual active scan. |
| Galaxy catalog | Integration owner | Prove catalog, sector indexes, route endpoints, and world identity together before enabling publication. |
| Native storage connector | Keel Developer #3, separate checkout | Preserve stock routing, collision, and lifecycle. Deliver independent commits and a lab plan. |

The integration owner wires Plugin.cs, Program.cs, documentation indexes, version, changelog and the isolated test profile. Merge feature slices only after deterministic validation, drift tests, and an inventory of supported and unsupported cases. Do not publish from this worktree.
