# Galaxy catalog probes

The preview probe compiles a stock subset and an authored candidate without changing the loaded galaxy. Set `SEVO_KEEL_GALAXY_PREVIEW_PROBE=1` to run it.

## Disposable publication and reload

Build this sample against the Keel 0.1.10 candidate. Run it in the verified Keel generation lab on Steam build 26451593 / 26w45a with BepInEx 5.4.23.5. The publication probe checks the lab's Unity company and product names and its managed profile before using stock character UI.

Give the launcher a fresh managed state directory. In it, create an empty `galaxy-characters` directory and give it one file named `sevo-galaxy-save-root.json`. Use schema version 1 and the same profile ID as the state's `sevo-runtime-profile.json`. Set `stateDirectory` to the absolute managed state path and `nativeSaveRoot` to the absolute `galaxy-characters` path. The sample redirects the native character path to this root only in its disposable process and reapplies the redirect after stock path initialization.

Set `SEVO_KEEL_EXPERIMENTAL_GALAXY_CATALOG=1`. Start a fresh process for each row. Keep the same managed state for the authored creation, reload, and refusal runs.

| `SEVO_KEEL_GALAXY_PUBLICATION_PROBE` | `SEVO_KEEL_GALAXY_PUBLICATION_ACTION` | Expected result |
|---|---|---|
| `authored` | `create` | Stock character creation loads a two-star catalog. Native gate lookup resolves both directions and the stock character writer saves. |
| `authored` | `load` | Stock character selection reloads the character and catalog with the same identity. |
| `mismatch` | `reject-load` | A changed destination seed refuses the pinned character. The native character file stays byte-identical. |
| `missing` | `reject-load` | An absent declaration refuses the pinned character. The native character file stays byte-identical. |
| `authored`, publication opt-in disabled | `reject-load` | A disabled provider refuses the pinned character. The native character file stays byte-identical. |
| `subset` | `create` | In a separate empty managed profile, stock-derived stars and routes reach the native consumers. |
| `conflict` | `reject-create` | In a separate empty managed profile, two declarations refuse the request before a native character directory is created. |
| `authored` | `cancel-create` | In a separate empty managed profile, a deliberately last-running HarmonyX prefix cancels the original stock call. The provider is never called, assets keep their stock references, and no catalog pin, session lease, or native character directory is created. |
| `authored` | `disable-create` | In a separate empty managed profile with publication enabled at launch, the sample disables publication before character entry. The remaining guard refuses the marked root without changing the catalog or save. |
| `missing` | `stock-create` | In a separate empty disposable profile, omit the native save-root marker. Stock character creation and save proceed with the original asset references and no pin or lease. |

Look for `KEEL_GALAXY_PUBLICATION_PROBE PASS`. The fixture waits for the starting system and player to settle before saving. It exercises native gate lookup in both directions. Flying through the physical gate requires a separate transit test. Set `SEVO_KEEL_GALAXY_PROBE_GAME_MODE=survival` to create or check a Survival character through the same stock entry flow. Creative remains the sample default. [Current galaxy verification status](../../docs/KEEL-GALAXY-CATALOG-PREVIEW.md)

Use `SEVO_KEEL_GALAXY_PROBE_DRAIN=1` in a rendered lab if the known stock end-of-frame job stall prevents progress. The sample also defers phase zero of the Creative tutorial for its exact test character and save root so the lab can observe the normal initial respawn. Both are sample-only diagnostics.
