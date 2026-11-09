# Selectable pass-through storage probe

Run in the verified disposable connector lab. Use a new managed solo creative world and the exact session-owned stock boot character. Load this sample beside the candidate Keel runtime.

Set `SEVO_KEEL_PASS_THROUGH_STORAGE_FIXTURE=1` and `SEVO_KEEL_PASS_THROUGH_STORAGE_FIXTURE_GAME` to the absolute isolated game path. Also set `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1` and set these capability flags to `1`:

- `SEVO_KEEL_EXPERIMENTAL_FLUIDS`
- `SEVO_KEEL_EXPERIMENTAL_STORAGE_CONNECTORS`
- `SEVO_KEEL_EXPERIMENTAL_SELECTABLE_PASS_THROUGH`
- `SEVO_KEEL_EXPERIMENTAL_FLUSH_STORAGE_PLACEMENT`

Use the usual managed session and state variables. The fluids hook provides the safe fixture tick for observing stock item transfer. It does not change the transfer medium.

The sample registers an owned stock corner, a separate pass-through flush fitting, and a passive sensor sleeve with no storage role. It checks each published collider shape for physical collision bypass and verifies the original stock definitions are still collidable. It then places the corner and uses the native overlap query to reject an exact duplicate and a solid over the fitting. It selects the exposed corner using the ordinary stock voxel ray and submits a stock delete for that exact hit. It also rejects a fitting over an existing solid, then checks that the flush fitting can be selected before the second cargo endpoint is placed and transfers 4 of 12 items through its bidirectional stock graph.

Filter BepInEx records by `KEEL_PASS_THROUGH_STORAGE|<session>|`. Require `armed`, `colliders`, `duplicate-rejected`, `normal-removed`, `reverse-rejected`, `flush-ray`, `linked`, and `pass` in order. The final record must include `source=8;sink=4`. The fixture leaves the owned grid in the disposable world.

The placement rejections use the stock pre-charge overlap query. Deletion uses the normal ray result and stock voxel delete submission. The fixture does not exercise the delete-button hold UI or a Survival debit, and the scoped flush adapter remains Creative-only.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `1e11fdca7532406a9582fb9b571da753`. The owned corner, flush fitting, and sleeve published collision-off flags while the stock definitions retained their ordinary flags. The native overlap query rejected both the duplicate and cargo intersection, and the inverse check rejected a fitting intersecting existing cargo. The ordinary ray selected the corner for stock deletion and selected the exposed flush fitting. The flush route was bidirectional and the two cargo units held 8 and 4 items after a stock pull.

Rendered session `645155c238a245dea2432d66f8d0a5e1` repeated the test with the native flush symmetry guard. The stock character capsule query at the same grid location returned 0.0000 m of correction for the exposed fitting and 0.3411 m for stock cargo. This checks the game's physical contact query. No player movement was injected.
