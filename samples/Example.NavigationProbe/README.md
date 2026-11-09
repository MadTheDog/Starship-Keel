# Native navigation probe

`NavigationExamples` shows how to attach a HUD marker to a live grid and select an existing stock tracked entity.

The optional native fixture uses the existing isolated Sublight waypoint scenario for the stock pilot, moving Hotwing and separate passive contact. It neither creates nor drives the two ships. Run it only with the verified connector lab and the waypoint fixture, using a new managed solo creative character.

Keep the usual managed and Sublight waypoint environment, including its exact lab identity and session character. Load this sample DLL alongside the candidate runtime and the Sublight sample. Set:

- `SEVO_KEEL_NAVIGATION_FIXTURE=1`.
- `SEVO_KEEL_NAVIGATION_FIXTURE_GAME` to the verified isolated game directory.
- `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1`, `SEVO_KEEL_EXPERIMENTAL_NAVIGATION_MARKERS=1`, and `SEVO_KEEL_EXPERIMENTAL_NAVIGATION_TARGETS=1`.
- `SEVO_KEEL_SUBLIGHT_PROBE_AUTOMATIC=1` and `SEVO_KEEL_SUBLIGHT_PROBE_WAYPOINT=1`, along with the existing Sublight waypoint launch settings.

Filter the BepInEx log for `KEEL_NAVIGATION|<session>|`. The fixture requires one exact session-owned anchor, a visible stock waypoint widget driven with the marker's transform and local offset, more than 2 m of resolved marker movement, and an active stock target ID that resolves back to the exact tracked contact. It then disposes the marker and verifies the widget was destroyed. Require `marker-added`, `native-widget-visible`, `native-widget-moved`, `native-target-selected`, `marker-disposed`, and `pass`. Target selection can occur before or after the widget observations.

The `hud-late-update` diagnostic reports whether the native HUD callback ran while native-container access was unsafe. Record the lab's render-end fallback separately if it appears in the run.

Rendered acceptance on Steam build ID 26451593 / 26w45a passed in session `66f192079d864a4ab44bb58c2042a112`. A native waypoint widget resolved 6.43 m of anchor movement and was destroyed after disposal. The stock target HUD selected the exact tracked contact. The lab enabled its diagnostic render-end fallback. The HUD callback reported no active job lock during the observed marker frames.
