# Starship Keel API

The SDK and runtime are separate libraries. `Sevo.Framework.Sdk` has no Unity dependency; `Sevo.Framework.Runtime` is a BepInEx plugin adapting the current game's actual contracts. Both target netstandard2.1. See the two independent, buildable [examples](../samples/README.md).

## Declare content

Dependent plugins declare their requirements before native resource initialization:

```csharp
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id)]
// In the dependent plugin's Awake:
ContentRegistration.Register("example.alpha", "hull", 1,
    id => StockShape.CreateHull(id, "Example Alpha Hull"));
```

The durable identity is `example.alpha:hull`, independent of its display name. The runtime supplies the numeric ID. Content revision1 is an explicit contract; changing it requires migration support, not a silent reassignment. Late or duplicate declarations reject.

The original v0.1.0 native API supports ordinary stock hull definitions. Keel 0.1.2 adds [functional blocks](KEEL-FUNCTIONAL.md) with mod-selected native templates, native placement callbacks, and one sustained logic input. Mods own their block definitions and gameplay. Compound shapes, custom native geometry, world migrations and networked content need additional capabilities.

## Allocation and persistence

`ResourceSpace(kind, capacity, reserved, occupied)` describes one resource kind. `ContentAllocator(spaces, lineage)` validates and applies a complete desired-content snapshot with `Apply(requests)`. Bricks and collision templates have separate capacities/occupancy. Requests use stable namespaced keys and positive revisions.

The allocator preserves existing assignments, sorts new keys deterministically, tombstones missing keys, and never automatically reuses their IDs. A failed batch leaves the previous state intact. Persisted schema1 stores lineage, key, owner, resource kind, numeric ID, revision and tombstone. Unknown revisions, malformed mappings, occupied saved slots and exhaustion reject rather than silently substitute content.

Native registration reserves and persists the whole mapping before executing factories. The factory must return the exact supported native object published in the assigned slot. Ownership is checked around resource callbacks and reloads. Partial native mutation fails that content session permanently and requires restart; clearing one registry entry is not a reliable native rollback.

Numeric capacity is bounded by the live registry, packed ID representation and relevant native ID-indexed buffers. The preview reserves0 and conservatively1;1 is a policy exclusion pending its full semantics. No recorded list of346 vanilla definitions is used as an allocation allowlist. Future vanilla occupation of a persisted ID is an actual conflict to handle, not permission to move saved identities silently.

## Profile and launch protocol

The host creates a marked runtime-state directory containing `sevo-runtime-profile.json` schema1 with profile UUID and exact game directory. Each process receives `SEVO_PLATFORM_STATE_DIR` and a fresh `SEVO_PLATFORM_SESSION`. The runtime validates ownership, directory containment and registry lineage before persistence.

`runtime-status.json` reports schema, nonce, actual process ID, profile, contracts, admitted registrations, resource readiness, failures, native capacities/occupancy, per-feature results and diagnostic MVID. The launcher rejects another session's report. `resourcesReady` means this observed resource cycle passed the framework's checks; it does not prove world/blueprint admission or every native subsystem's success.

Default mode is observational audit: no factories run and `registry.json` is not changed. Experimental content requires a valid managed session plus explicit opt-in, documented in the sample guide. Keep it in disposable profiles. Broad missing-mod admission gates are not implemented.

## Future compatibility

The runtime validates the methods, signatures, lifecycle call relationships, relevant native allocations and stock bindings it uses. It records build/version/MVID/hash identities for diagnostics without admitting or rejecting by them. Synthetic MVID/version changes pass; actual required dependency changes reject their affected capabilities. A changed dependency pattern may require adapter review even when a developer believes behavior is equivalent.

No test establishes compatibility with every future release. Keep the SDK surface small and versioned, isolate game-specific changes in the adapter, and expand through demonstrated use cases.
