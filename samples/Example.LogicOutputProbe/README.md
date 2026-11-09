# Native logic output evidence fixture

Use an isolated managed solo world. Enable Keel's experimental functional blocks and logic outputs, and launch the lab with `SEVO_KEEL_LOGIC_OUTPUT_FIXTURE=1`. The managed launch must supply `SEVO_PLATFORM_STATE_DIR` and a distinct `SEVO_PLATFORM_SESSION`. Load this sample DLL alongside the candidate Keel runtime. The existing surface sensor does not need to be used.

## Build the rig

1. On a single test grid, place **Probe Native Logic Fixture**, one stock Switch and three stock Event Gates. Start with the Switch off.
2. Use the stock Link Tool to link Switch → fixture and fixture → each of the three gates. The fixture must have exactly these three output links, with one source link on each gate.
3. Configure the first gate for **Keel Signal**, Toggle mode, inverted. Set the second to **Keel Pulse**, non-inverted, with a pulse length of about one second. Set the third to **Keel Value**, comparison **greater than**, threshold **5**, non-inverted. Keep all three in ordinary event mode without a fire group.
4. Save the rig while the Switch is off if you plan to test disk reload. Save outside the probe sequence so a restored run starts with zero outputs.
5. Turn the Switch on. The fixture reads the live native link and gate job records before publishing values. It drives a high signal, one pulse and a value of 7.25, checks that the pulse ends, sends a low signal with 2.5, then leaves a high signal for the teardown check. The fixture reports a failure if it cannot observe the expected native states before its deadline. It also checks that a duplicate pulse from the same rendered frame is refused.
6. After a pass, remove the fixture while its source signal is high and leave the gates in place. The fixture attempts to observe all three source fields at zero during native despawn. Inspect the gates with the stock Link Tool to confirm the links were removed. This manual observation covers the link cleanup separately from the logged output reset.

## Inspect the evidence

Run `Read-LogicOutputEvidence.ps1 -LogPath <isolated BepInEx LogOutput.log> -Session <SEVO_PLATFORM_SESSION> -RequireDespawnReset`. Omit the final flag to validate propagation alone. The reader rejects missing, repeated, out-of-order, or unexpected state records. It reports whether native despawn reset was observed. A timeout or absent record does not establish behavior.

To verify disk restoration, relaunch the saved rig with a new managed session ID, enable the fixture again, and turn the Switch on without relinking the gates. Use the same reader with `-RequireLoaded` and the new ID. This verifies the restored source and channel selections by exercising them through the native gates. Stock control is independent. Repeat a regular stock switch and gate setup in the same world.

The fixture uses existing placed commands and reads public live job records. It does not spawn or rewrite links or reconfigure the gate. Keel capability admission, game launch, and the rendered fixture observations are separate verification steps.
