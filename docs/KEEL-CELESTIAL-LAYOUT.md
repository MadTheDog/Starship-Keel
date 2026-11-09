# Celestial layout declarations

Enable the experimental Celestial Layout capability in a disposable managed solo profile with EnableCelestialLayout or SEVO_KEEL_EXPERIMENTAL_CELESTIAL_LAYOUT=1. Register declarative changes before the first solo body generation.

```csharp
CelestialLayoutProvider.Register("example.scenarios", "orbit",
    CelestialLayoutRule.OverrideBody(systemSeed, bodySeed, orbitRadiusMeters: 2500000));
```

OverrideBody accepts an exact system and body seed with a radius, orbital radius, or both. AddRockyMoon accepts a system seed, stock planet seed, new moon seed, radius, and orbital radius. AddRockyPlanet accepts a system seed, new planet seed, radius, and orbital radius around the primary star. Units are metres. Native IDs and ordering remain stable for stock bodies. New bodies are appended in registration key order. The stock introduction system and primary star are protected. Radius overrides require an unringed body. Orbits around a ringed parent require validated ring clearance. The hook validates body type, radius, parent, orbital clearance and stock body-count limits. If a system's declarations conflict or cannot be applied together, it keeps the stock layout and logs the contributing keys.

Keep declarations fixed for the lifetime of a disposable test world. The preview has no migration for existing saves, and multiplayer is unsupported. Mods supply their own data. Keel passes the same resolved list to the native system and mission consumers.

The Example.CelestialLayout probe checks stock ID preservation, a moon and planet addition, and agreement between the native system, mission and point-of-interest lookups in an isolated solo session. Follow the [sample's acceptance steps](../samples/Example.CelestialLayout/README.md) to exercise destination loading and saved-world reload.

On Steam build 26451593 / 26w45a, the disposable fixture loaded the destination through the native system transition and saved the character there. A fresh process opened the character through the stock picker and reproduced the same six-body catalog hash, including IDs, parents, sizes, positions, and seeds. The fixture used diagnostic render-end job completion.
