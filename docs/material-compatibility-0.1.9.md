# Keel 0.1.9 voxel material compatibility

- Capture Keel-owned rows and their diffuse and specular mip data before publishing. Verify current bindings by row and slice contents, including changes in place and append-only replacements.
- Keep the existing baked fallback for preparation failures. A detected conflict after publication keeps stock readiness callbacks running, marks managed resources failed, and blocks stock menu entry before session or save changes.
- Exercise copied and extended bindings, overwritten rows, altered and missing mip data, and unrelated table changes in the framework suite. Verify the complete game scenario in an isolated profile on Steam build 26451593 / 26w45a before calling the fix runtime-validated.

The contract covers registered attachment palettes. Any independent weapon attachment mod with custom textures can use it. Ordinary block surface registration can later share the same ownership model through a public registration contract.
