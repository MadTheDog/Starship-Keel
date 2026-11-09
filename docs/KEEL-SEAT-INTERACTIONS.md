# Keel seat interactions

Enable each independent capability in an isolated managed solo profile. Set `[Experimental] EnableSeatedPoseTargets` or `EnableDismountPoses`. The corresponding environment flags are `SEVO_KEEL_EXPERIMENTAL_SEATED_POSE_TARGETS=1` and `SEVO_KEEL_EXPERIMENTAL_DISMOUNT_POSES=1`.

Mods register seat providers in their dependent plugin's `Awake`. Each receives the current seat placement and returns a value only for seats it recognizes. Return `null` to defer to another provider or stock behavior. Providers resolve in ordinal content-key order. The first supplied value wins. Mods own marker blocks, persisted links, and offset selection.

- `SeatedPoseTargetProvider` supplies optional left and right foot positions in seat block-local coordinates. Targets must be finite, nearby, and within the local humanoid's ankle reach. The hook applies foot position IK only while the local player occupies a live seated command. Native body movement and hand IK continue independently.
- `DismountPoseProvider` supplies a proposed seat-local capsule center. The native exit animation and facing run as usual. At its final tick, the hook checks that the alternate point is near the stock exit and checks capsule clearance at bounded increments along the adjustment. Invalid, distant, or obstructed adjustments use the stock exit.

## In-game acceptance

Use the [seat probe](../samples/Example.SeatInteractionProbe/Example.SeatInteractionProbe.csproj). Its config offsets are fixture values and may need adjustment to the selected seat and scale.

1. Tag one ordinary seat `keel-seat-probe`. Leave another seat untagged. Enable both capabilities and verify their entries in runtime status.
2. In third person, occupy the tagged seat. Confirm that the player's feet follow the configured points, that both hands can still use the yoke, and that exiting or switching seats releases the feet. Confirm that an unreachable or nonfinite offset falls back to the stock pose.
3. Exit with a nearby clear candidate. Verify the final position follows the configured point. Place an obstruction along the adjustment and verify the stock exit is used. Repeat after moving the seat's parent.
4. Check that untagged seats use the normal pose and dismount. Disable one capability and confirm the other remains available.

The metadata tests exercise the native seat, animator and capsule call contracts. In a rendered disposable run on Steam build 26451593 / 26w45a, a clear custom exit moved the capsule 0.900 m from the stock exit. An obstructed candidate retained the stock exit, and the unrelated stock seat was unchanged. A separate fixture measured both requested foot targets in the native Animator at full weight, preserved the stock yoke hand targets and verified that foot weights cleared on exit. Pedal mesh fit and moving-grid exit behavior have separate acceptance steps above.
