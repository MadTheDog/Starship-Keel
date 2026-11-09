# Recovering a player-owned grid

Enable `[Experimental] EnableGridRecovery` or `SEVO_KEEL_EXPERIMENTAL_GRID_RECOVERY=1` in a managed solo profile. On the main game thread during normal loaded play, pass the current `ParentEntity` to `GridRecoveryService.TryRecover`.

Keel verifies that the same entity is still registered under its persistent grid ID and calls the game's ordinary recovery gate. The grid must be defeated, player-owned and repaired to the stock hull threshold. The call reports true only when the native gate accepts recovery. Use this from repair consoles or a vehicle recovery interaction.

NPC ownership transfer and generated world wreck conversion are separate gameplay transitions. The recovery call leaves eligibility and hull state with the game.

In a disposable solo world, verify that an eligible defeated player grid recovers after repairs, while a healthy grid, a grid below the native threshold, an NPC and a generated wreck do not. Unload the candidate and confirm a stale reference cannot trigger recovery.
