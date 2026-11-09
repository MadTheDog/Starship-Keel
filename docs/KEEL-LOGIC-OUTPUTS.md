# Logic outputs for functional blocks

Enable `[Experimental] EnableLogicOutputs = true` or `SEVO_KEEL_EXPERIMENTAL_LOGIC_OUTPUTS=1` in a managed disposable solo profile. Also enable Keel's normal experimental functional block capability. Runtime status reports `logicOutputs`.

Register a functional block as usual, then declare outputs for the same key during plugin `Awake`.

```csharp
ContentRegistration.RegisterFunctionalBlock("example.survey", "probe", 1, "Survey Probe",
    () => TabHull.HULL_A, context => new SurveyProbe(context));
LogicOutputProvider.Declare("example.survey", "probe");
```

Use the current placement's context to publish a held signal, one pulse per rendered frame, or a finite numeric value. Calls require the game thread and a native-container-safe phase during solo play. Every channel begins at zero on spawn and load. The owning mod decides how and when to reconstruct its outputs. On teardown, Keel resets the signal and numeric value and leaves the pulse edge bit unchanged until the native slot is released. A detached Event Gate stops polling the old Keel source slot.

```csharp
LogicOutputProvider.TrySetSignal(context, detected);
LogicOutputProvider.TrySetValue(context, distance);
LogicOutputProvider.TryEmitPulse(context);
```

Use the stock Link Tool from the output block to a stock Event Gate. The gate's event picker shows **Keel Signal**, **Keel Pulse**, and **Keel Value**. The stock Event Gate applies its usual signal settings and threshold comparator. Keel leaves stock event records and table sizes intact and uses three audited stable, vacant event slots for its own provider type. If the game or another plugin already owns one of those slots, Keel disables the adapter instead of changing what a saved ID means. Functional blocks can still load without their optional output adapter in a compatible managed profile. Restore the same compatible configuration for output-linked saves and blueprints.

A clearance sensor can publish terrain distance and the first contact pulse. A machine mod can publish a ready signal, completed pulse, and progress value through the same API. The [surface sensor sample](../samples/Example.LogicOutputProbe/Example.LogicOutputProbe.csproj) uses the [world surface query](KEEL-WORLD-SURFACES.md).

## Acceptance

In an isolated solo world, place the sensor and link three Event Gates. Select each channel and verify stock signal inversion, pulse propagation, and numeric thresholding. Cover and uncover the sensor, then remove it while its signal is high. Save and reload with the same profile and verify each gate still selects its original output. Repeat with an unrelated stock Event Gate. The Cecil test covers native schema, capacity, binding, and fail-closed metadata collisions. Runtime and save roundtrip results require the game fixture.
