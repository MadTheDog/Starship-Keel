# Native context and cargo action probe

For manual use, aim at an activatable command and press F9 to inspect it. Aim at a stock cargo unit and press F8 to open its native inventory panel. Both shortcuts are configurable.

The automated fixture requires the verified disposable connector lab. Start a fresh managed solo creative world and keep the camera still. If the starting camera ray is blocked, the fixture can move only the verified local test character once to a vacant nearby site and wait for a fresh stock ray. Load this sample DLL and set:

- `SEVO_KEEL_CONTEXT_CARGO_FIXTURE=1`.
- `SEVO_KEEL_CONTEXT_CARGO_FIXTURE_GAME` to the exact verified isolated game directory.
- `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1`, `SEVO_KEEL_EXPERIMENTAL_CONTEXT_ACTIONS=1`, and `SEVO_KEEL_EXPERIMENTAL_INVENTORY_VIEWS=1`.
- The ordinary managed `SEVO_PLATFORM_SESSION` and `SEVO_PLATFORM_STATE_DIR`.

The fixture places one separate stock cargo grid along the camera's unobstructed ray and leaves it in the disposable world. It reads the real stock ray hit and ActionActivate target from successive gameplay frames. Once it sees the same cargo in two frames, the previous action and its cargo context must both be refused. It invokes the fresh action, reads the exact native active storage ID from the stock cargo panel, and closes that panel through the stock UI.

Filter the BepInEx log for `KEEL_CONTEXT_CARGO|<session>|`. Require `armed`, `native-build`, `native-target`, `stale-refused`, `native-ui-open`, and `pass` in order. The target, refused stale action, open panel and pass must name the same positive storage ID. The final record reports one dispatch and `closed=True`. A fail or missing stage leaves rendered acceptance incomplete.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `9cccd8c73abd44fa98c0670167aa90c7`. A stock cargo ray resolved storage 9359. Prior-frame action and context were refused. The current action dispatched once, opened the matching native cargo panel, and closed it.
