# Station buy offers

Enable `EnableStationBuyOffers` in a managed solo profile, or set `SEVO_KEEL_EXPERIMENTAL_STATION_BUY_OFFERS=1` for that session. The hook is available only when Keel admits both native market price calls. Check `StationBuyOffers.IsAvailable`.

Call `StationBuyOffers.Register(ownerId, localName, quote)` during plugin startup. The callback receives the system seed, station SGUID, native station type and a copy of the native inventory item. Match a mod-owned item against its current registered identity. Return null to keep the stock bid or an integer from 1 to 1,000,000 in the game's native trade unit. The callback runs synchronously when the market builds the player's sell list. Keep it deterministic and free of side effects.

For example, a metals mod can match its registered Aluminium ore identity at a chosen refinery station. A construction mod can match Brick inventory items at a different depot. Existing market access and inventory selection apply. If two callbacks quote the same item, Keel logs the conflict and keeps the stock bid. Invalid or throwing callbacks are skipped with a warning.

Native trade code still moves the item and pays the quoted price. When a mod changes the bid and the game adds the purchased item to the station's resale list, Keel sets the resale price to at least what the station paid. The hook adds no initial stock, standalone kiosks, or persisted offer ledger.

The automated contract check admits the inspected Steam build 26451593 / 26w45a and rejects a changed sell-list method. Native UI and transaction acceptance require an isolated in-game test.
