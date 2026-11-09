# Starship Keel

- Keep Keel a general-purpose modding framework. Provide reusable contracts for mod authors and put concrete blocks, assets, effects, and gameplay in samples or their owning mods.
- Keep game and third-party reference binaries out of source control and release packages.
- Check the native contracts each capability uses. Keep game build IDs, versions, and hashes for diagnostics.
- Run framework tests and distinguish automated checks from in-game observations. Keep game tests in a dedicated disposable profile.

## Content authoring contract

- Read [Framework capabilities and limits](docs/FRAMEWORK.md), [the capability overview](README.md#current-scope), and [Custom blocks and voxel materials](docs/KEEL-MATERIAL-COMPATIBILITY.md) before designing a mod or changing a content API.
- Reuse documented registration surfaces and stock-backed fallbacks. Treat unmanaged writes to shared voxel material tables, cursors, GPU buffers, or shader texture bindings as outside the Keel authoring contract.
- A capability counts as usable only when its API is documented, the profile enables it, and the runtime audit admits it. Record static, automated, and runtime evidence separately. Do not infer support for ordinary custom block palettes, multiplayer synchronization, or future game builds from an attachment test.
