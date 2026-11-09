# Keel terrain generation providers

Keel 0.1.10 exposes bounded terrain parameters before stock density publication. A mod can adjust crater distribution on a ring's asteroid templates. A separate mod can reduce canyon relief on selected terrestrial bodies. Both supply typed proposals through the same provider API.

| Capability | Managed profile setting | Launch flag |
|---|---|---|
| `planetTerrainGeneration` | `[Experimental] EnablePlanetTerrainGeneration` | `SEVO_KEEL_EXPERIMENTAL_PLANET_TERRAIN_GENERATION=1` |
| `asteroidTerrainGeneration` | `[Experimental] EnableAsteroidTerrainGeneration` | `SEVO_KEEL_EXPERIMENTAL_ASTEROID_TERRAIN_GENERATION=1` |

Register a provider in a dependent BepInEx plugin's `Awake` with `TerrainGenerationProvider.RegisterPlanet` or `RegisterAsteroid`. Pass your owner ID, a local name, and a callback that returns `PlanetTerrainOverrides` or `AsteroidTerrainOverrides`. The callback receives the stock parameters, body or parent body seed and ID, and the stock starting-planet marker. Return `default` or leave an individual parameter null to decline it. Keep callbacks deterministic and free of game mutations. A generation call seals its corresponding provider catalog for the rest of the process.

| Proposal | Native range, inclusive |
|---|---|
| Planet crater amount | 0 to 1 |
| Planet continent threshold | -0.5 to 0.5 |
| Planet canyon strength | 0 to 1, when stock canyon frequency, warp, and smoothing are available |
| Ring asteroid template crater amount | 0 to 0.5 |
| Ring asteroid template crater scale factor | 0.25 to 0.5 |

Each callback sees the original parameter snapshot. Keel calls providers in ordinal key order and applies a parameter only when one provider claims it. Conflicting declarations retain the native value for that parameter while disjoint declarations still apply. An exception declines that provider's proposal for the current call. Keel logs the first failure for each provider. If native parameter ranges differ from the supported contract, Keel keeps the affected native value and logs the mismatch.

The planet hook covers the stock planet construction and regeneration entry point. The asteroid hook covers planetary ring templates shared by stock asteroid instances. Keel leaves seeds, world identity, terrain row structure, ore, material identities, climate, ocean properties, and physics to their owning native or Keel contracts. The adapter validates the managed construction and publication paths, native instance initialization, and density compute buffer bindings independently for the two capabilities.

Use a new disposable solo world. Keel applies provider proposals only while native solo mode is active. Use the same providers and settings if you reload that world. Save migration, already edited terrain reconciliation, multiplayer, and shader output acceptance are outside the preview.

[Example.TerrainGenerationProviders](../samples/Example.TerrainGenerationProviders/Example.TerrainGenerationProviders.csproj) checks the resulting parameters in live native planet and ring tables after a solo system is entered. A system without planetary rings only exercises the planet assertion.
