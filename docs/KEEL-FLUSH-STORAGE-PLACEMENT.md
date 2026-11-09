# Creative flush item fitting prototype

The optional `flushStoragePlacement` capability uses stock Tank Join geometry with an item connector role. It is a development probe for isolated managed Creative solo profiles. The ordinary short item fitting remains available through the regular storage connector API.

The preview keeps the native collision traversal. Only the query-local property copies for an explicitly registered flush fitting and its exact same-subgrid stock cargo face receive the native join exception. A port must lie on a face within 5 mm and point into the cargo far enough to satisfy stock storage port matching. The fitting can be installed on the first exposed cargo face. Placing a second stock cargo against the other side requires the first live aligned cargo to remain in place.

The decoded native build action repeats the checks before it mutates the grid. When native collision finds no overlap, a bounded query on the same subgrid still checks for valid touching cargo faces. The gate applies to the fitting and to cargo placements on a grid that has contained one. Creative avoids a late rejection after Survival inventory consumption. The gate rejects mirrored placements affecting a fitting and leaves normal collisions in the native path. The native connector owns storage graph construction, removal and load reconstruction.

## Rendered acceptance

1. Enable native storage connectors and `flushStoragePlacement` in a disposable managed Creative solo profile. Confirm both resource admission and placement capability.
2. Place a stock cargo unit, place the fitting on an exposed flat face, then add the second stock cargo at the opposite aligned port. Verify stock item transfer in both directions and separate inventories.
3. Verify rejection for an empty stand-alone fitting, wrong role, misalignment, cross-subgrid overlap, blocked geometry, and mirrored placement. Test each attempted build without changing stock behavior around ordinary cargo elsewhere.
4. Remove each endpoint and the fitting in turn. Check selection and deletion while both cargo units are flush, especially if the fitting mesh is occluded. Test duplicate or delayed build commands while an endpoint is removed.
5. Save and reload the exact Creative fixture using the same registered fitting and profile registry. Verify that both cargo ports reconstruct and that removal still tears down the native graph.

Static audits verify managed query dispatch, the exact native collision call and the build commit path. On Steam build 26451593 / 26w45a, a disposable rendered Creative fixture placed a fitting between two stock cargo blocks, transferred items through both ports, removed the fitting, and verified transfer resumed after replacement. The fixture used diagnostic completion of stalled stock render-end jobs. Interactive preview, Survival and multiplayer still require separate acceptance. Keep the prototype disabled by default and keep its saves with their Creative profile.

A separate two-process Creative fixture saved the native grid after a pull left 8 items at the source and 4 at the sink. Stock character reload recovered the same grid key, fitting ID, both native links, and both quantities. A fresh pull after reload left 4 items at the source and 8 at the sink. The lab used diagnostic render-end completion in both processes.
