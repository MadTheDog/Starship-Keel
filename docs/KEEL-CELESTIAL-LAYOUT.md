# Celestial layout declarations

Enable the experimental Celestial Layout capability in a disposable managed solo profile with EnableCelestialLayout or SEVO_KEEL_EXPERIMENTAL_CELESTIAL_LAYOUT=1. Register declarative changes before the first solo body generation.

```csharp
CelestialLayoutProvider.Register("example.scenarios", "orbit",
    CelestialLayoutRule.OverrideBody(systemSeed, bodySeed, orbitRadiusMeters: 2500000));
```

OverrideBody accepts an exact system and body seed with a radius, orbital radius, or both. AddRockyMoon accepts a system seed, stock planet seed, new moon seed, radius, and orbital radius. Units are metres. Native IDs and ordering remain stable for stock bodies. New moons are appended in registration key order. The stock introduction system and primary star are protected. Radius overrides require an unringed body. Orbits around a ringed parent require validated ring clearance. The preview validates body type, radius, parent, orbital clearance and stock body-count limits. If a system's declarations conflict or cannot be applied together, it keeps the stock layout and logs the contributing keys.

Keep declarations fixed for the lifetime of a disposable test world. The preview has no migration for existing saves, and multiplayer is unsupported. Mods supply their own data. Keel passes the same resolved list to the native system and mission consumers.

The Example.CelestialLayout probe checks stock ID preservation, a moon addition, and agreement between native direct and mission lookups in an isolated solo session. Actual destination loading and saved-world reload are separate checks.
