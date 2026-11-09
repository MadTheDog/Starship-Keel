# Keel sublight control lease

The experimental `sublightControl` capabilities let independent mods provide analog flight input to eligible unoccupied spaceships in managed solo Creative and Survival profiles. A remote handset can map operator input to the lease. An autonomous docking approach can supply analog axes using its own guidance rules. The mod owns targeting, control policy and presentation.

The lease requires `gridPhysics`, an audited stock sublight path and a managed solo profile with `EnableSublightControl` enabled. Use `SublightControlService.TryAcquire(ownerId, grid, out lease)` on the game thread. A live lease accepts `lease.TrySubmit(translation, rotation)` using the same per-component [-1, 1] axes as stock sublight control. Submit at least every 350 ms of unscaled time. Dispose the lease when the controller stops. Successful submission queues an input for the next eligible simulation step. Stock behavior determines the resulting acceleration and rotation.

Only the exact registered, locally owned, dynamic spaceship is eligible. For `TryAcquire`, the target must be the player's last piloted ship and the stock post-exit control loop must finish. Pending stock entry and control by any stock pilot take precedence. Loss of power, authority, live core, suitable drive mode or attachment state ends the lease. Opening a menu pauses the managed physics callback and the input deadline continues to run. Lease end ceases further flight input, so existing momentum follows stock physics.

For an earlier ship, check `SublightControlService.PreparedShipsAvailable` and call `TryAcquirePreparedShip(ownerId, grid, out lease)`. The target must be within 250 m in the same loaded system and retain its stock flight computer and matching live yoke orientation after ordinary piloting. The local player must be out of stock seats and command sessions. A remote handset can switch between prepared ships, or a docking controller can maneuver an earlier prepared service ship.

For a ship whose stock pilot session has not initialized a computer, check `SublightControlService.UnpilotedShipsAvailable` and call `TryAcquireUnpilotedShip(ownerId, grid, out lease)`. This uses the same nearby authority, power and attachment checks and requires exactly one live stock computer with a matching linked stock yoke. A launch controller can reposition a newly built ship, and a rescue tug mod can remotely move a newly loaded service craft. The lease uses stock force and torque and keeps its controller history separate. The ship remains unoccupied, and normal stock entry can take over. All three acquisition methods share one lease and the same bounded analog input.

These contracts expose standard sublight. Camera, ship possession, tactical systems, weapons, docking controls, warp and lightcruise use their own stock or mod paths. For unpiloted flight the lease checks native power availability. Thruster particles tied to a stock pilot session and fuel-use parity are outside the verified contract. The lease and its input are transient across reload.

## Evidence and disposable acceptance

The standalone suite tests axis bounds, independence from assembly MVID, and rejection of altered drive and stock post-exit call paths. A rendered disposable solo fixture on Steam build 26451593 / 26w45a loaded the bundled V27 Hotwing and entered and exited through stock authority. It refused the lease before piloting, while piloting, and during the stock post-exit loop. After the exit settled, the lease increased forward velocity by 0.311 m/s while native control mode stayed unoccupied. The fixture returned the pilot to the recorded starting point and removed its test ship. The lab used diagnostic job completion and a temporary Creative tutorial deferral.

A separate rendered stress run verified that stale input was refused after expiry and that stock re-entry revoked a fresh lease. The fixture timed out during cleanup after the second exit.

The prepared-ship fixture piloted and exited two stock Hotwings in the same disposable solo session. The original API declined the earlier ship. The prepared lease moved it forward by 0.270 m/s while native control mode remained unoccupied and pilot ID remained zero. The fixture removed both ships through stock cleanup.

The unpiloted contract suite also checks that changes to the stock drive constructor, force orientation, thrust refresh branch and temporary state backing fields disable the affected capability independently.

The rendered Creative fixture in session `56d9bc64670a459d9c32f7f9bde041c4` measured a forward gain of 0.299 m/s on the never-piloted ship, then verified stock entry and exit and a 0.385 m/s gain through the prepared lease on that same ship. The Survival fixture in session `3f264c0b3f8f496a834ea69e9733e1e2` measured a 0.541 m/s forward gain before first entry. In both modes the ship stayed unoccupied throughout the unpiloted pulse. The Survival fixture staged its own character in clear space after stock initial placement. Its later stock first-flight help window paused the cockpit handoff, which still needs a complete run.

Use a locally owned powered test ship with a stock computer and unobstructed free space to extend the acceptance checks.

1. Call the original `TryAcquire` before piloting, while piloting and immediately after exiting. Verify rejection. Wait until native exit damping completes, then acquire and compare translation and rotation against stock sublight behavior.
2. Try acquiring from a second controller and verify exclusivity. Stop input and verify the lease expires. Acquire again, reenter the ship and verify stock input takes precedence without a second drive update.
3. Repeat with power loss, computer removal, docking, unload and quit. Verify the old lease never resumes after reopening the world.
4. Compare fuel use and control-mode flags before and after the experiment.
5. In separate Creative and Survival sessions, acquire the unpiloted lease before first stock entry. Verify forward acceleration while pilot ID stays zero, the native computer remains unset, and no orientation or thrust totals are retained. Release, enter and exit through stock, then verify the prepared lease still works.
