# Grid destruction notifications

Keel 0.2.0 exposes an experimental observer for stock gameplay destruction of an initialized grid in an active solo session. The capability targets Steam build ID 26451593 / 26w45a and checks its native contract separately from other hooks.

Enable `[Experimental] EnableGridDestruction` or set `SEVO_KEEL_EXPERIMENTAL_GRID_DESTRUCTION=1`. `GridDestruction.IsAvailable` reports whether the hook is installed. Delivery also requires an active local server, an initialized grid, and the game thread.

```csharp
using Sevo.Framework.Runtime;

GridDestruction.RegisterObserver("example.combat", "record-destruction", notice =>
{
    recordedDestructions.Enqueue(notice);
});
```

Register once per process using an owner ID and local name. Keel invokes callbacks in ordinal order by their combined registration key. Keep each callback short and queue work that needs to run outside the stock destruction call. Keel logs a callback failure and continues with the remaining observers.

`GridDestructionNotice` contains the grid SGUID, its display name, the star seed, its stock NPC flag, and its generated-wreck flag. Keel copies these values immediately before the server destruction routine. The SGUID and display name can be empty, and a star seed of zero means the transform has no known star. The notification can be retained without keeping game objects alive.

Keel delivers after the server destruction routine returns with the same grid marked destroyed, before the caller sends the stock visual destruction RPC. Repeated calls for a grid already marked destroyed produce no additional notice. A grid that recovers and is later destroyed can produce another notice. Native save loading, generated wreck initialization, client visual callbacks, despawning, and quitting fall outside this delivery path.

A combat telemetry mod can count or display destruction events by star and NPC flag. A salvage mod can record candidate SGUIDs for a later eligibility check. The mod owns candidate retention and eligibility rules, and the notice grants no authority over the grid.

The [isolated probe](../samples/Example.GridDestructionProbe/Example.GridDestructionProbe.csproj) creates a disposable grid through the stock builder, tests one destruction notice and a repeated call, then checks that stock cleanup produced no additional notice.
