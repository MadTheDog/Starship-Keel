# Native camera and grid ownership handoff

The investigation used the private native references for Steam build 26451593 / 26w45a. Rendered runtime evidence is called out separately.

## Remote camera

`EnterCommandCamera` can attach the local view to a stock camera without moving the player rigidbody. Native `ActionActivate.EnterCommand` records the entered command and sets `PlayerState.SystemEntered`, which excludes normal building. Its exit path sends the stock exit request and clears the local entered state before the queued native exit restores the view. That leaves a window where the build ray can still originate from the remote camera. The native exit check also dereferences the stored command's entity, and stock destruction only handles the fully entered state. A reusable camera session needs action exclusion until exit acknowledgment and cleanup of the exact live command before its anchor disappears. The stock tracking camera can display an entity model in its own preview.

## Claiming an existing wreck

The native client authority call assigns simulation authority. Persistent owner and behavior are supplied at instance loading. `BlockEntitySync` consumes the initialization RPC once, and changing NPC behavior before removal can skip stock NPC deregistration. Generated wrecks also have a terrain-node attachment and are omitted from the ordinary system instance save while attached.

The stock saved-instance loader can create a player-controllable object with an existing persistent grid key. A replacement transaction needs to record provenance, stage the ordinary instance, retire the source through stock cleanup, and verify unique identity after reload. For terrain-generated wrecks, the source-node save and replacement-instance save are independent writes, so interruption and retry require explicit recovery. The native ownership predicate also excludes grids with world-wreck metadata. Claiming a generated wreck therefore needs a validated metadata transition and durable retirement of the source node. Keel's existing grid recovery contract revives an eligible defeated player-owned grid through the stock recovery transition.

The disposable wreck probe exercised the ordinary replacement path in rendered Creative and Survival solo profiles. Creative saved and reloaded a defeated player-owned grid under the source's exact key. Survival moved only the disposable character to clear orbit, deliberately stopped after retirement, recovered grid `6DAECC26-ADF3E` from the staged body and metadata in a second process, and verified one live grid and one stock save entry in a third process. [Wreck probe sessions and checks](../samples/Example.WreckClaimProbe/README.md#runtime-evidence)

The deliberate interruption took place before any replacement save. The native body and system index are separate writes, and the probe stops if it sees an unindexed body. Terrain-generated wrecks can also respawn from the node when their stock body fails to load. A public claim contract still needs verified recovery across these transitions, durable retirement for respawning sources, and preservation checks for more complex grids.
