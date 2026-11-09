# Grid physics callbacks and forces

Enable `[Experimental] EnableGridPhysics = true` or `SEVO_KEEL_EXPERIMENTAL_GRID_PHYSICS=1` in a managed disposable solo profile. Runtime status reports the independent `gridPhysics` capability.

Register a namespaced callback during plugin startup.

```csharp
GridPhysicsStep.Register("example.guidance", "forces", step =>
{
    if (!TryResolveGuidance(out var grid, out var localPoint, out var worldForce)) return;
    GridForceService.TryApplyAtGridPoint(step, grid, localPoint, worldForce);
});
```

The callback runs at most once per rendered game frame, immediately before the native rigidbody simulation is scheduled. `DeltaTime` is the game simulation frame interval in seconds. Registration closes when the first eligible simulation frame runs. A callback that throws is stopped and logged. The context expires when its callback returns.

Pass a currently loaded independent parent grid, an application point in that grid's local coordinates, and the force in world axes. Keel validates the native handle still belongs to the grid and applies the force with the game's normal force-at-position path, including torque and wake handling. Attached, static, kinematic, force-sleeping, stale, or non-authoritative bodies are rejected. Each callback may submit up to 64 nonzero forces per frame. The force limit is 1,000,000,000 in native units and the grid-local point limit is 4,096 metres from its origin. Do not scale force by the frame interval before applying it. The game integrates it during the step.

A guideway mod can compute lateral and vertical corrections for a captured pod and let along-track travel remain free. A compliant strut mod can compute spring and damping force from a contact ray and apply it where the leg mounts. Both can use the independent [world surface query](KEEL-WORLD-SURFACES.md) capability. The owning mods decide which grids and blocks to drive, how to capture or release guidance, and how much force to apply.

The [read-only probe](../samples/Example.GridPhysicsProbe/Example.GridPhysicsProbe.csproj) logs the first valid phase and applies no force. For acceptance, a disposable fixture should verify a small force on an independent grid, torque at an offset, rejection while locked, and no force after a callback expires. The Cecil tests cover game metadata, phase ordering, native force accumulator calls, and independence from assembly identity. They do not establish runtime behavior.
