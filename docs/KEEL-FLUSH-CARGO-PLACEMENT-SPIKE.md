# Flush cargo placement spike

Static inspection target: Steam build 26451593 / 26w45a, Keel 0.1.10 development checkout. The stock-backed short item fitting remains the supported placement shape for this slice. Exact flush placement requires an isolated follow-up.

## Native placement and collision

- `BrickProperties.IsOverlapValid` recognizes the Tank Join exception only when one side carries `isTankJoin` and the other carries `hasTank`. The predicate receives property copies and two transform IDs. It has no per-brick endpoint identity.
- The managed placement call chain is `VoxelPhysics.IsOverlapping` → `SkyPhysics.QueryOverlapAabb.Execute` → `BVHSet<T>.OverlapAabbAligned/OverlapOBB` → constrained `IQueryCollector<T>.OnElementIntersect`. The inspected methods use managed IL without a Burst compile attribute. Harmony dispatch through the constrained collector still requires rendered verification.
- The `BrickColliderBVH.Element` collector overload retains the proposed brick, exact overlapping brick data, and both transform IDs before calling `IsOverlapValid`. A narrowly scoped adapter could change only the query-local property copies for one registered flush item fitting and one exact same-subgrid cargo endpoint. Native overlap handling would still run for other colliders. Both placement orders need equivalent checks.
- Native storage port matching only tests whether the port position plus a short normal offset enters the candidate cargo bounds. The placement exception needs stricter face alignment, distinct ports and live identity. The shared face is usually hidden after both cargo units have been placed. A native-like workflow places the join on the first exposed face, then places the second cargo unit.

## Build, inventory, and reconstruction

`ActionBuild.LeftClickUp` checks cached placement validity, consumes survival inventory, and sends the action through `NetworkedBuilder`. The native command relays the payload to an RPC. The RPC invokes `DoAction`, which calls `EntityBricks.Add` directly. A preview-only exception cannot prove the endpoints still match when the action applies. A late veto also has to account for inventory already consumed and suppress the stock success callback. The inspected path offers no reservation or refund at that commit point.

After a valid placement, `EntityBricks` dispatches native pipe construction, addition, initialization and removal. Loaded entities replay `OnEntityInitialized(..., onLoad: true)`. The existing storage connector contract can preserve the native graph lifecycle once the placement contract has been verified.

A creative-solo probe can isolate these questions because stock build quantity consumption only mutates inventory in Survival. The probe needs an audited scoped collector adapter and a revalidation gate where the deserialized native action applies. Test both orders, mirrored placement handling, unrelated collisions, endpoint deletion, fitting selection and removal, and disk reload. Survival admission needs a separate inventory transaction or equivalent proof of safe consumption and commit.
