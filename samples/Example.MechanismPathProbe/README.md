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

The fixture samples the live native child transform over successive rendered frames and checks both orthogonal legs, return travel, fixed rotation and the original travel plane. Use the manual probe for obstacle response, stock comparison, and disk reconstruction.
