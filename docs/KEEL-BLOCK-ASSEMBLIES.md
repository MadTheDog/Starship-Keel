# Block assemblies and shipyard blueprint requirements

Enable `[Experimental] EnableBlockAssemblies` or `SEVO_KEEL_EXPERIMENTAL_BLOCK_ASSEMBLIES=1` in a disposable managed solo profile. Call these APIs on the game thread when native containers are available. Check the capability report before relying on them.

An assembly binds a live controller to explicitly selected, named placements on the same loaded subgrid. Mods decide how players select the parts and which block types, orientations, and layouts qualify. The binding preserves selection order and rejects empty selections, duplicate names or placements, missing or destroyed bricks, and candidates on another subgrid. It validates the native brick ID, instance index, grid position, rotation, scale, and grid level each time it resolves. Matching these fields verifies current placement. An identical replacement that reuses the same index can match again.

Use `BlockAssemblyProvider.TryBind(grid, controller, selectedParts, out var assembly)` after obtaining the placements. `assembly.IsComplete()` performs an on-demand check without allocating a snapshot. `assembly.TryGetParts(out var parts)` returns a read-only snapshot for inspection. Resolve again before gameplay uses the selected parts. Store the mod's own selections by its save format and rebuild the binding after world reload. The runtime binding holds the live grid reference and is scoped to that loaded grid. It performs no world scan or per-frame polling.

## Shipyard blueprint guard

Enable `[Experimental] EnableShipyardAssemblyEligibility` or `SEVO_KEEL_EXPERIMENTAL_SHIPYARD_ASSEMBLY_ELIGIBILITY=1` alongside block assemblies. `ShipyardAssemblyEligibility.TryRequireAssembly(yard, resolve, out var handle)` attaches a temporary requirement to one live native shipyard command. One requirement per yard is supported. Keep the handle and dispose it when removing the requirement. The resolver returns a binding for that yard's controller or null when the assembly is incomplete. It should stay fast and avoid side effects. Register again for a new command after reload.

The hook checks preview eligibility and the actual blueprint spawn action. The commit check runs before the stock charge and spawn dispatch. Existing size, inventory, payment and other native checks still apply. Saved ship restoration and lost ship recovery use other actions and are outside this blueprint requirement. The selected assembly provides eligibility only. It leaves construction, visuals, and part semantics to the mod.

The [sample library](../samples/Example.BlockAssemblyProbe/AssemblyExamples.cs) provides callable examples for a frame-and-welder shipyard and a furnace module. Wire those calls to your own selection flow.

## Arc Furnace work rate

Enable `[Experimental] EnableMachineWorkRateModifiers` or `SEVO_KEEL_EXPERIMENTAL_MACHINE_WORK_RATE_MODIFIERS=1` alongside block assemblies and the [machine recipe adapter](KEEL-MACHINE-RECIPES.md). Pass a complete assembly whose controller is a live Arc Furnace to `MachineWorkRateModifier.TryAttach(assembly, multiplier, out var handle)`. The multiplier must be finite and between 0.25 and 4. One modifier per furnace is supported. Dispose the handle to release the slot, and bind again after world reload.

The modifier applies to future powered ticks of Keel custom recipes. It preserves earned progress if a module disappears and returns the rate to 1× while the selected assembly is incomplete. The stock power draw per tick stays in place. Higher work rates can reduce energy per batch because fewer powered ticks are needed. Each tick still checks input and output headroom, and a batch can cross the native completion boundary only once. Stock recipes, the Crucible, and the Crusher retain their existing rates.

## Acceptance in a disposable solo profile

1. Register a yard requirement before opening its blueprint picker. Select a valid frame and welder and confirm stock preview and blueprint spawn. Remove the welder between preview and confirmation and verify no ship is spawned and inventory and cash are unchanged. Re-select a replacement and confirm eligibility returns.
2. Repeat with another stock yard and verify it retains stock behavior. Verify wrong-grid or destroyed parts fail, a reloaded yard needs a fresh binding, and a disposed requirement stops applying.
3. Resolve a furnace module, remove it, and query again. Confirm the previous snapshot cannot be used as proof of current presence.
4. Compare one custom Arc Furnace batch with and without the sample 2× module. Confirm both batches consume the declared input and produce one output stack. Stall power during a batch and confirm progress pauses. Remove the module mid-batch, then confirm the remaining work progresses at the stock rate.

Offline metadata checks exercise the bounded brick lookup and native blueprint charging and dispatch contracts. They do not exercise live placement or transaction behavior.

The rendered machine sample compared two powered Arc Furnace batches on Steam build 26451593 / 26w45a. The module advanced the sample recipe at twice the stock rate. Removing it restored the stock rate while preserving progress already earned. Both machines produced exactly three dust. A separate rendered Creative run called the native shipyard eligibility method. A complete assembly retained stock eligibility, and missing or removed parts blocked it. Actual blueprint construction and Survival resource accounting have separate acceptance steps above.
