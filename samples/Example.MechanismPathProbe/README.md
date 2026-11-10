# Stock slider path probe

The [manual probe](../../docs/KEEL-MECHANISM-PATHS.md) binds stock sliders tagged **keel-path-probe** during native child creation or reconstruction. To test movement in an empty lab, use the separate optional fixture below.

1. Verify the game path, Unity company and product, and the LocalLow junction target in the isolated lab identity record. Load the sample DLL alongside the candidate Keel runtime. Use a fresh disposable solo creative world.
2. Launch with a managed profile supplying `SEVO_PLATFORM_STATE_DIR` and a fresh `SEVO_PLATFORM_SESSION`. Set these additional variables before launch.

   | Environment variable | Value |
   | --- | --- |
   | `SEVO_PLATFORM_EXPERIMENTAL_CONTENT` | `1` |
   | `SEVO_KEEL_EXPERIMENTAL_MECHANISM_PATHS` | `1` |
   | `SEVO_KEEL_MECHANISM_PATH_FIXTURE` | `1` |
   | `SEVO_KEEL_MECHANISM_PATH_FIXTURE_GAME` | Absolute game directory from the lab identity record |
   | `SEVO_KEEL_MECHANISM_PATH_FIXTURE_COMPANY` | Unity company from the lab identity record |
   | `SEVO_KEEL_MECHANISM_PATH_FIXTURE_PRODUCT` | Unity product from the lab identity record |

3. Enter the fresh world and leave the fixture alone. It creates a small, separate grid about 25 metres above the player. It selects only its own slider placement before the stock builder creates the child, links a stock Switch, and uses that Switch to drive the stock carriage out and back. It leaves the grid in the disposable world.
4. Filter the isolated BepInEx log for `KEEL_MECHANISM_PATH|<SEVO_PLATFORM_SESSION>|`. Require the ordered stages `bound`, `outbound`, `returned`, and `pass`. The outbound record must give both offsets equal to half the bound travel within 0.002 m. Both returned offsets must be within 0.002 m of zero. Both movement records must report `facing=fixed;legs=2`. Any `fail` or missing stage requires inspection.

The fixture samples the live native child transform over successive rendered frames and checks both orthogonal legs, return travel, fixed rotation and the original travel plane. Use the manual probe for stock comparison and disk reconstruction.

## Built-in clearance preview

Set the sample's `Probe.UseBuiltInSweep` configuration to `true` and enable mechanism paths, connected rails and stock rail tools in the managed solo profile. Check that Keel reports `stockRailTools` available. The same tagged slider follows the authored right angle using Keel's collider query. The stock game supplies straight extensions. This sample supplies the turn as slider-local geometry.

Use the empty fixture for a traversal check. For a manual obstruction check, put a solid block at the outside of the second leg where only the payload can reach it. Drive the slider until it stops, remove the obstacle, and drive it again. The built-in check may stop early when the swept bounds overlap geometry that the actual payload would clear. The native penetration fixture below exercises the older provider and expects native rollback after contact. Leave `UseBuiltInSweep` disabled for that fixture.

## Optional obstruction fixture

Set `SEVO_KEEL_MECHANISM_PATH_FIXTURE_OBSTRUCTION=1` along with the variables above. Use a fresh disposable character whose name ends in a dash and the first eight characters of `SEVO_PLATFORM_SESSION`. The lab's solo server must run in `dontListen` mode.

The fixture waits for a settled player and a clear site in the active star. It adds a stock hull payload to the moving child and verifies baseline out-and-back travel before placing a stock hull obstacle on the slider's parent grid. The obstacle is wider than the payload across the second leg, so the native surface points can enter the obstacle beyond the stock penetration threshold.

Require `bound`, `outbound`, `returned`, `blocked`, `resumed`, and `pass` in order. The `blocked` record must report native penetration above 0.015 m, one native movement-finished event, a restored intermediate state and fixed facing. The fixture checks the restored pose remains stable, removes the exact obstacle through the stock builder, and retries the path. Successful cleanup removes the obstacle. As in the original fixture, the rest of the test grid remains in the disposable world. Use a fresh world after a failed run.

The `site` and `obstacle` records identify the native star, collider IDs and placed bounds. The `native-stop` record captures the actual stop even if the rollback assertions fail. An `obstacle-timeout` record includes sampled travel, matching active native queries, peak penetration and minimum distance between collider centers.

Rendered obstruction acceptance on Steam build ID 26451593 / 26w45a passed in session `9cccd8c73abd44fa98c0670167aa90c7`. The stock collision reached 0.0162 m penetration and emitted one native stop. The fixed-facing carriage held at (1.000, 0.505) on the second leg, then resumed after the exact obstacle was removed.
