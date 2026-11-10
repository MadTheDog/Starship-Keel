# Station and generated site hooks

These Keel 0.2.3 candidate hooks are experimental and individually enabled in a managed solo profile. Check each API's `IsAvailable` before using it. Use the `Experimental` config keys below or their session environment equivalents, prefixed with `SEVO_KEEL_EXPERIMENTAL_`. Register declarations during plugin startup.

| Capability | Config key | Environment suffix |
| --- | --- | --- |
| Station buy prices | `EnableStationBuyOffers` | `STATION_BUY_OFFERS` |
| Station stock and kiosk access | `EnableStationStock` | `STATION_STOCK` |
| Station service actions | `EnableStationServices` | `STATION_SERVICES` |
| Station patrol pools | `EnableStationTraffic` | `STATION_TRAFFIC` |
| Orbital and deep-space points | `EnableOrbitalPoints` | `ORBITAL_POINTS` |
| Terrain building placement | `EnableSurfaceSites` | `SURFACE_SITES` |
| Generated site loot | `EnableGeneratedPoiLoot` | `GENERATED_POI_LOOT` |
| Station mission types and rewards | `EnableStationMissions` | `STATION_MISSIONS` |
| Faction definitions and reputation | `EnableModFactions` | `MOD_FACTIONS` |

## Markets and services

`StationStock.RegisterItem` stocks a namespaced Keel item, including contextual ore or ingot identities. `RegisterBrick` takes a live native Brick definition ID. Both accept quantity, price, and a predicate over system seed, station identity and station type. The hook appends in ordinal key order to the stock lists of existing fuel and trading stations. It runs once per resident station instance and uses the native inventory and payment paths. A station can regenerate its stock after unload or restart. The API provides no persisted restock schedule.

`StationStock.TryOpenKiosk` opens the market for an eligible native station root. A mod can call it from its own local interaction, such as a counter or terminal, and owns access to that interaction. The hook requires an existing eligible market. [Buying items from players](KEEL-STATION-BUY-OFFERS.md) is registered separately.

`StationServices.Register` adds an action to one of the two unused slots in the native station hub action row when the row is visible. Keel admits matching declarations in ordinal key order while a slot remains. The mod supplies the label and interaction. A survey service can provide coordinates, and a refinery can open its own screen.

`StationTraffic.RegisterPatrolPool` contributes ship blueprints by native faction and tag. The hook deduplicates full blueprint paths. The native station role, patrol eligibility, count, and squadron behavior apply. A security fleet and a faction escort pack can each contribute ships.

## Orbital points and surface sites

`OrbitalPoints.Register` takes a stable key, system seed, planet seed, stock station or deep-space POI template type, display name, and a planet-centered offset in system axes and metres. Keel appends an entry using the first eligible matching stock blueprint in that system. The introduction system is protected. Keel derives a stable SGUID from the system and declaration key and skips points that overlap a body, an existing point, or a prior declaration. Keep the declaration key and placement stable after creating a world. The native spawner owns the blueprint, modules, and station services.

`OrbitalPoints.RegisterBlueprint` accepts a mod-owned native blueprint and consortium for the same placement contract. The root module must match the declared station type for stations. Keel skips missing blueprint files. A custom refinery and an abandoned survey platform can each supply a separate blueprint.

`SurfaceSites.TrySpawn` takes a stable key, resolved terrain anchor pose, native blueprint metadata path, name and yaw. It requires both world surface queries and terrain anchors. The mod chooses when to attempt placement and owns the blueprint and site selection. Keel checks slope, four footprint corners, a live grid identity and the native entity save path before spawning a small persistent terrain building. A mining camp and a survey outpost can use the same hook. Orbital station services, docking, and map entries on terrain buildings require further integration.

`GeneratedPoiLoot.Register` supplies a registered item key, quantity and predicate over a newly spawned Keel site. Keel inserts the item into the first native storage unit and marks the grid dirty. Use the predicate to restrict a declaration to a particular generated site. Native saved instances are intended to bypass the fresh-entity path. Loot persistence and refill behavior require an isolated world test.

## Missions and factions

`StationMissions.Register<TMission>` takes a station predicate and a public native `MissionBase` subclass with a `(BrickEntityPOIData, ulong)` constructor. The mod owns objectives, positive availability weight and the native `rewards` fields. Keel adds at most one entry of each registered type to a station board and publishes a stable namespaced tag to the game's save loader. Keep tags stable. If a mod is absent, the native loader skips an unresolved mission tag. Examples include a survey contract and a cargo request.

`ModFactions.Register` publishes a standard native faction definition through the game's mod faction input. The native SGUID includes the declared faction name, so keep the name and key stable. Native world generation seeds territory when no territory save exists. Existing saved territory uses the native SGUID map. `TryAddReputation` applies a signed reputation change to a registered and loaded faction. Keep the same faction mods enabled when reopening a save that uses them.

All hooks have independent native contract audits against the Steam build 26451593 / 26w45a reference. Automated checks exercise admission and rejection. Live gameplay and fresh-process reload must be verified before relying on the hooks in an existing save.
