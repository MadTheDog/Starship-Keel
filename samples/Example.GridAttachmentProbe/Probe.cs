using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Sevo.Framework.Runtime;
using UnityEngine;

namespace Sevo.Example.GridAttachmentProbe
{
    [BepInPlugin(Id, "Keel Grid Attachment Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.gridattachment";
        private static ConfigEntry<KeyboardShortcut>? attachKey;
        private static ConfigEntry<KeyboardShortcut>? releaseKey;
        private Harmony? harmony;

        private void Awake()
        {
            if (PrepareNativeFixture()) return;
            attachKey = Config.Bind("Probe", "Attach", new KeyboardShortcut(KeyCode.F8),
                "Request a stock lock on the grid hit by the targeted Mag-Lock.");
            releaseKey = Config.Bind("Probe", "Release", new KeyboardShortcut(KeyCode.F9),
                "Request release of the targeted Mag-Lock.");
            var targetSguid = Config.Bind("Probe", "ExpectedGridSguid", "",
                "Optional preview filter. The stock command still chooses the acquired grid.");
            ContextActionProvider.Register(Id, "attach", "Attach grid", context =>
                GridAttachmentService.TryGetCandidate(context.Placement, out _), context =>
            {
                if (!GridAttachmentService.TryGetCandidate(context.Placement, out var grid) || grid == null) return;
                if (targetSguid.Value.Length != 0 && grid.SGUID != targetSguid.Value)
                {
                    Logger.LogInfo("Grid attachment probe skipped an unexpected target.");
                    return;
                }
                if (GridAttachmentService.TryRequestAttach(context.Placement, out var recordedGridId))
                    Logger.LogInfo($"Native Mag-Lock attach request recorded for {recordedGridId}.");
            });
            ContextActionProvider.Register(Id, "release", "Release grid", context =>
                GridAttachmentService.TryGetAttachedGrid(context.Placement, out _), context =>
            {
                if (GridAttachmentService.TryRequestRelease(context.Placement))
                    Logger.LogInfo("Native Mag-Lock release request recorded.");
            });
            harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(typeof(PlayerActions), nameof(PlayerActions.UpdateCommand)),
                postfix: new HarmonyMethod(typeof(Probe), nameof(AfterPlayerActions)));
        }

        private void OnDestroy() => harmony?.UnpatchSelf();

        private static void AfterPlayerActions()
        {
            var key = attachKey?.Value.IsDown() == true ? Id + ":attach"
                : releaseKey?.Value.IsDown() == true ? Id + ":release" : null;
            if (key == null) return;
            var action = ContextActionProvider.GetAvailableActions().FirstOrDefault(candidate => candidate.Key == key);
            if (action != null) ContextActionProvider.TryInvoke(action);
        }
    }
}
