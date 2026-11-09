# Claim a transient NPC wreck

Keel 0.2.0 exposes an experimental ownership handoff in managed solo Creative and Survival profiles. Enable `[Experimental] EnableWreckClaim` or set `SEVO_KEEL_EXPERIMENTAL_WRECK_CLAIM=1`. Check the `wreckClaim` runtime capability before offering a claim action.

A scripted salvage encounter can award the wreck after a fight. A convoy mod can let the player claim a defeated escort after the mission releases it. Both use the same handoff. The caller creates and owns the NPC source, retires any independent respawn rules, and decides when the player qualifies. Keel grants ownership to the active local player.

```csharp
if (WreckClaimService.TryClaimTransientWreck(wreck, out var operation))
{
    // Keep operation.PersistentId and poll operation.State on the game thread.
}
```

Call on the game thread once the character's current system is loaded and any menu has closed. The source must be a defeated, pilotless transient NPC ship loaded with local player authority, with no crew, squadron, mission link, docking, or terrain attachment. The current stock system index must exist, and the original position needs clear space for a replacement. The API uses the ordinary native character save root. The opt-in is independent of grid recovery.

A true return means Keel staged a validated snapshot before retiring the source. Treat the source reference as stale after that point. `Pending` covers stock retirement, loading, and persistence. `Claimed` exposes the new live grid with the original persistent ID, defeated state, and local build permission. `NeedsAttention` includes a reason and retains the recovery journal. A false return leaves the source in place. Persist the ID if the mod needs to find the ship after reload.

Keep the native character directory intact during a pending operation. When the same managed profile and character revisit the original system, Keel reopens its journal, verifies the snapshot, and uses the stock index to avoid a second live grid. It can resume an incomplete write of its own exact payload. A conflicting file, unexpected live grid, or unreadable stock index holds the operation for inspection. One claim can be pending per character.

The ownership handoff leaves the ship defeated. A mod can separately request [grid recovery](KEEL-GRID-RECOVERY.md) once the game allows it. Stock-generated terrain wrecks, crewed encounters, and persistent or independently respawning NPCs require separate lifecycle support. See the [disposable probe](../samples/Example.WreckClaimProbe/README.md) for the in-game checks.
