# Starship Keel changelog

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
