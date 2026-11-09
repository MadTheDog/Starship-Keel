# Block assembly probe

`AssemblyExamples` shows how a mod can supply the selected parts for a stock shipyard or furnace. Selection belongs to the mod. This fixture supplies it explicitly and uses stock blocks as stand-ins for a frame and welder.

The optional native shipyard fixture requires a fresh managed solo creative world in the verified connector lab. Use the session-owned stock boot character. Load the sample DLL beside the candidate runtime and set:

- `SEVO_KEEL_SHIPYARD_ASSEMBLY_FIXTURE=1`.
- `SEVO_KEEL_SHIPYARD_ASSEMBLY_FIXTURE_GAME` to the exact isolated game directory.
- `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1`, `SEVO_KEEL_EXPERIMENTAL_BLOCK_ASSEMBLIES=1`, and `SEVO_KEEL_EXPERIMENTAL_SHIPYARD_ASSEMBLY_ELIGIBILITY=1`.
- The ordinary managed `SEVO_PLATFORM_SESSION` and `SEVO_PLATFORM_STATE_DIR`.

The fixture creates one stock shipyard grid and calls its real `CanSpawnBlueprint` preview with the same synthetic metadata each time. It reads Harmony's original-execution flag to distinguish a Keel denial from the native decision. It never commits a blueprint or opens a user blueprint file.

Read `KEEL_SHIPYARD_ASSEMBLY|<session>|` in the BepInEx log. Require this order:

| Stage | Returned | Native body ran |
| --- | --- | --- |
| `native-baseline` | True | True |
| `empty-denied` | False | False |
| `incomplete-denied` | False | False |
| `complete-stock` | True | True |
| `deleted-denied` | False | False |
| `replacement-denied` | False | False |
| `requirement-released` | True | True |

Require a final `pass`. The replacement is a different stock brick at the deleted part's position. The fixture does not claim to distinguish an identical replacement that reuses the same native slot and placement. Creative exercises the stock preview decision. Survival resource accounting and actual spawn dispatch require separate evidence.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `791d06a782e64fd5ba87328e2806c9da`. The actual stock eligibility method ran for baseline, complete assembly, and released requirement. Empty, incomplete, deleted, and replaced assemblies returned false before the native preview. The synthetic metadata was never committed as a blueprint.
