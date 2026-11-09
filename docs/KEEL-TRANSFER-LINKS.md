# Temporary native storage links

Enable `EnableTransferLinks` or `SEVO_KEEL_EXPERIMENTAL_TRANSFER_LINKS=1` in a managed solo profile. At a native-container-safe game-thread boundary, call `TransferLinkService.TryConnect` with the two live hose base placements and a maximum distance in metres. This is a storage graph link. A loading arm or refuelling boom can provide its own model, controls and arrival or departure rules.

Keel checks that the hose bases are on different player-owned grids, have the same native transport type and grid level, and have an unused remote port. The link uses the game's normal item or fuel routing. While linked, both remote ports are reserved. Dispose the returned lease to schedule unlinking at the next safe frame. Leaving range or deleting an endpoint releases the reservation. The lease does not become a saved stock logic-tool link, so a mod must re-establish it after reload.

## Solo acceptance

Use two disposable player-owned grids. Connect an item hose pair and issue a native transfer between their inventories. Repeat with compatible fuel hoses. Confirm mixed media, occupied ports and excessive range are rejected. Release during a transfer, move the grids apart, and delete each endpoint in separate trials. Verify unrelated routes and stock same-grid hose links continue working. Save and reload and verify a temporary link has not become a persistent connection.

The offline contract checks exercise graph layout, insertion, cleanup and failed native dependency checks. They do not prove cross-grid routing in a loaded game.
