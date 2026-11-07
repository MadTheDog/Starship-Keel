# Keel functional blocks

This API is available in Starship Keel 0.1.2. It was tested on the Windows x64 Mono player on Steam build **26451593 / 26w45a**, Unity 6000.6.6f1. Keel validates the native contracts at startup and records the build identity for diagnostics.

## Declare a block

Reference `Sevo.Framework.Sdk.dll` and `Sevo.Framework.Runtime.dll` from the [Keel release](https://github.com/MadTheDog/Starship-Keel/releases/tag/v0.1.2). Declare a minimum BepInEx dependency of `0.1.2` on `Sevo.Framework.Runtime.Plugin.Id`. Register during the dependent plugin's `Awake`.

```csharp
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using VoxelEngine;

ContentRegistration.RegisterFunctionalBlock(
    "example.signal-lamp", "indicator", 1, "Signal Indicator",
    () => TabHull.HULL_A,
    context => new IndicatorBehavior(context));
```

`IndicatorBehavior` derives from `FunctionalBlockBehavior`. The factory creates one behavior per placement. Use `OnSpawn()` to initialize it, `OnLoad()` for a restored placement, `OnSustainedInput(bool enabled)` to receive held input changes, and `OnDespawn()` to release owned resources. Restored placements receive `OnSpawn()` before `OnLoad()`. A placement starts off. An active placement receives `false` before cleanup. If a callback throws, Keel stops that placement and logs its content key.

Use `context.TryGetPlacement(out BrickCoord placement)` on the game thread to resolve the current grid, position, orientation and scale. A false result means that the context has expired or the placement is unavailable. Keep physics and rendering in the mod's own verified update path and use the callback to maintain its enabled state.

Keel stores the assigned numeric brick ID under `ownerId:localName` in the managed profile. Keep the same key and revision when switching from the 0.1.1 preview registration method to `RegisterFunctionalBlock` with equivalent geometry and behavior. Use a new key when replacing an ordinary brick with a command block. A removed definition retains its reservation so another mod cannot inherit that ID.

## Model and logic

The mod chooses an existing native brick using a read-only selector. Keel calls it after the stock resource pass and before reserving content IDs. Keel can reuse single meshes with a static collider and baked materials, along with their placement facing, grid limits and resize settings. Compound and repeated shapes, dynamic colliders and materials, and transport templates are outside this API's current contract. A failed selector or an incompatible template skips that definition and logs its content key and reason. The mod owns the effect, rendering, physics, and configuration.

Each block accepts one directly linked stock Switch or Event Gate on the same entity. Set the Event Gate to **Input Signal / Toggle** with a compatible native event source, such as the Yoke's primary fire input. High enables the behavior. Low, unlinking or rebinding the gate disables it. Pulse, numeric and fire-group output modes do not drive the sustained input. After load, a linked Event Gate must deliver low before it can enable the placement. A player can also reconnect the output link. Restored Switch links wait for a fresh state transition.

The managed resource pass reuses baked stock materials after the normal stock pass. This preserves the native material cursor and keeps existing assigned IDs stable. Keel reports shared input and resource capabilities in `runtime-status.json`. The resource-ready report concerns resource initialization only.

## Test in an isolated profile

Launch the disposable lab through a managed Uplink ticket with `SEVO_KEEL_EXPERIMENTAL_FUNCTIONAL=1`, or enable `[Experimental] EnableFunctionalBlocks` in `local.sevo.modplatform.cfg` for the lab. Either enables experimental content in that managed session. In audit mode, Keel collects declarations without assigning IDs or creating blocks.

The release's functional lab probe passed on the target build in a rendered creative-solo session. It covered two caller-selected templates, isolated rejection of an unsupported template, native placement, Switch and Event Gate input, in-memory reconstruction, and cleanup. Keep experimental registration scoped to managed disposable solo profiles. Disk saves across processes, Event Gate save restoration, blueprints, and multiplayer need separate acceptance tests.
