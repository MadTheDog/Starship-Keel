# Starship Keel changelog

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
