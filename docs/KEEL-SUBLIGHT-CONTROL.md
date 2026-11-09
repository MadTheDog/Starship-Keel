# Keel sublight control lease

The experimental `sublightControl` capability lets independent mods provide analog flight input to the local player's last piloted spaceship after stock exit completes. A remote handset can map operator input to the lease. An autonomous docking approach can supply analog axes using its own guidance rules. The mod owns targeting, control policy and presentation.

The lease requires `gridPhysics`, an audited stock sublight path and a managed solo profile with `EnableSublightControl` enabled. Use `SublightControlService.TryAcquire(ownerId, grid, out lease)` on the game thread. A live lease accepts `lease.TrySubmit(translation, rotation)` using the same per-component [-1, 1] axes as stock sublight control. Submit at least every 350 ms of unscaled time. Dispose the lease when the controller stops. Successful submission queues an input for the next eligible simulation step. Stock behavior determines the resulting acceleration and rotation.

Only the exact registered, locally owned, dynamic spaceship is eligible. The stock computer and orientation must have been initialized through normal piloting in the current session. Acquisition waits for the stock post-exit control loop to finish. Pending stock entry and control by any stock pilot take precedence. Loss of power, authority, live core, suitable drive mode or attachment state ends the lease. Opening a menu pauses the managed physics callback and the input deadline continues to run. Lease end ceases further flight input, so existing momentum follows stock physics.

This first contract exposes standard sublight only. Camera, ship possession, tactical systems, weapons, docking controls, warp and lightcruise use their own stock or mod paths. A newly loaded ship must first be piloted and exited normally. The lease and its input are transient across reload.

## Evidence and disposable acceptance

The standalone suite tests axis bounds, independence from assembly MVID, and rejection of altered drive and stock post-exit call paths. A rendered disposable solo fixture on Steam build 26451593 / 26w45a loaded the bundled V27 Hotwing and entered and exited through stock authority. It refused the lease before piloting, while piloting, and during the stock post-exit loop. After the exit settled, the lease increased forward velocity by 0.311 m/s while native control mode stayed unoccupied. The fixture returned the pilot to the recorded starting point and removed its test ship. The lab used diagnostic job completion and a temporary Creative tutorial deferral.

Use a locally owned powered test ship with a stock computer and unobstructed free space to extend the acceptance checks.

1. Try acquiring before piloting, while piloting and immediately after exiting. Verify rejection. Wait until native exit damping completes, then acquire and compare translation and rotation against stock sublight behavior.
2. Try acquiring from a second controller and verify exclusivity. Stop input and verify the lease expires. Acquire again, reenter the ship and verify stock input takes precedence without a second drive update.
3. Repeat with power loss, computer removal, docking, unload and quit. Verify the old lease never resumes after reopening the world.
4. Compare fuel use and control-mode flags before and after the experiment. Live re-entry, expiry, and fuel-use comparisons still require separate checks.
