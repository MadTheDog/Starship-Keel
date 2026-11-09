# Keel contextual interactions

Use `ContextActionProvider` to register a namespaced action for the native crosshair command. Keel supplies a target only when stock targeting found a live, reachable, manually activable command during the current input frame. Mods decide when to present and invoke actions. The stock pick wheel has a fixed layout and this API leaves its layout intact.

```csharp
ContextActionProvider.Register("example.logistics", "open-cargo", "Open cargo",
    InventoryViewService.CanOpen, context => InventoryViewService.TryOpen(context));
```

Register on the game thread from the dependent plugin's `Awake`. Predicates should be quick and side-effect free. On the game thread after native player input has run, call `GetAvailableActions()` and present the returned keys and labels. Call `TryInvoke(action)` in the same input frame in which the action was returned. It revalidates the action's target and availability before invoking the callback. No action is available while the game has blocked activation, while loading, or outside a managed solo session. A presentation spanning multiple frames should refresh the list each frame in which it can invoke actions. A provider exception is logged and affects that evaluation only.

`InventoryViewService` opens the stock cargo view through the existing cargo command. It rechecks the live brick and storage ownership tuple. It accepts the currently targeted cargo command, with the stock interaction reach and permissions. Opening arbitrary storage by numeric ID, remote linked cargo, machine configuration panels, and multiplayer UI are outside this preview.

## In-game acceptance

Enable `[Experimental] EnableContextActions` and `EnableInventoryViews` in an isolated managed solo profile. Confirm that runtime status reports both capabilities and load only the [probe DLL](../samples/Example.ContextInteractionProbe/Example.ContextInteractionProbe.csproj).

1. Aim at a reachable cargo container and press the probe's F8 key. Confirm that the stock cargo window opens with the same inventory as normal activation.
2. Use F8 while aiming at a tank, a distant or removed cargo container, or while a menu blocks activation. Verify no cargo opens.
3. Aim at a reachable switch or other manually activable command and press F9. Verify that the probe records the targeted brick ID and that normal stock activation still works.
4. Disable inventory views only. Verify that the F9 interaction still works and that the F8 action is unavailable.

The standalone metadata test verifies native call relationships and identity-independent admission. A rendered disposable run on Steam build 26451593 / 26w45a rejected a prior-frame action, then used a fresh action to open and close the exact targeted native cargo inventory.
