# Rocky-body environments

Keel 0.2.0 exposes an experimental environment hook for rocky planets and moons in managed solo profiles. Enable `EnableBodyEnvironment` in the BepInEx configuration or set `SEVO_KEEL_EXPERIMENTAL_BODY_ENVIRONMENT=1`. A dependent plugin declares changes during `Awake`.

```csharp
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

// The mod supplies the destination star seed and the seed of a rocky body in that system.
var target = new BodyEnvironmentTarget(starSeed, bodySeed);
BodyEnvironmentProvider.Register("example.environments", "research-moon", target,
    new BodyEnvironmentOverride(gravityScale: 0.2f, atmosphereDensity: 0.5f, oxygenDensity: 0.24f));
```

The target uses seeds from the body catalog. Use the actual star and body seeds for the profile, rather than a transient native index or a display name. Matching rules apply wherever that star/body pair is generated during the solo process. Registration closes on the first eligible rocky-body generation. A duplicate declaration key is rejected. When multiple declarations set the same field on the same body, Keel logs the conflicting keys and leaves that input to stock generation. Other fields can still apply. Stock generation recalculates dependent settings from the resolved inputs.

| Input | Contract |
| --- | --- |
| `waterAmount` | A native generation input. Use zero for dry or a number above 0.01 and at most 1 for an ocean. The game derives its ocean radius and biome inputs from this value. |
| `atmosphereDensity`, `oxygenDensity` | Finite values from zero to one. If either is overridden, the oxygen output is capped at the resulting atmosphere density. This includes the stock starting-planet oxygen branch. |
| `gravityScale` | Nominal gravity relative to Earth gravity, from zero to four. The stock distance falloff remains active. |
| `atmosphereRgb`, `oceanMainRgb`, `oceanSecondaryRgb`, `oceanDepthRgb` | Packed `0xRRGGBB` colors. Each value replaces its corresponding native palette entry. |

The runtime audits the native environment stores, dependent generation order, and gravity publication before enabling the hook. Keel applies water, atmosphere, and palette inputs before native biome generation and publishes gravity through the stock physics row. The hook operates at generation time. Use a disposable solo profile and keep its mod versions and environment configuration with the save because changed settings can regenerate terrain differently. World migration, live edits, arbitrary terrain palettes, gas giants, and multiplayer synchronization are outside this preview.

The `Example.BodyEnvironmentProbe` sample registers a wet rocky planet and a low-gravity moon in the stock starting system. Build it against the isolated Keel build, enable `SEVO_KEEL_BODY_ENVIRONMENT_PROBE=1`, and enter a new disposable solo world. Look for `KEEL_BODY_ENVIRONMENT_PROBE PASS`. It checks the published native terrain and physics values, ocean node radii, palette colors, and the native rendering data. It does not measure rendered frames or gameplay movement.
