using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Sevo.Framework.Runtime;
using UnityEngine;

namespace Sevo.Example.ContextInteractionProbe
{
    [BepInPlugin(Id, "Keel Context Interaction Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.contextinteraction";
        private static ConfigEntry<KeyboardShortcut>? cargoShortcut;
        private static ConfigEntry<KeyboardShortcut>? inspectShortcut;
        private Harmony? harmony;

        private void Awake()
        {
            cargoShortcut = Config.Bind("Probe", "OpenCargo", new KeyboardShortcut(KeyCode.F8),
                "Opens the targeted stock cargo panel.");
            inspectShortcut = Config.Bind("Probe", "InspectCommand", new KeyboardShortcut(KeyCode.F9),
                "Logs the targeted stock command's brick ID.");
            ContextActionProvider.Register(Id, "cargo", "Open cargo", InventoryViewService.CanOpen, context =>
            {
                if (InventoryViewService.TryOpen(context)) Logger.LogInfo("Context action opened native cargo.");
            });
            ContextActionProvider.Register(Id, "inspect", "Inspect command", _ => true,
                context => Logger.LogInfo("Context action inspected brick ID " + context.Placement.brickData.brickId));
            harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(typeof(PlayerActions), nameof(PlayerActions.UpdateCommand)),
                postfix: new HarmonyMethod(typeof(Probe), nameof(AfterPlayerActions)));
        }

        private void OnDestroy() => harmony?.UnpatchSelf();

        private static void AfterPlayerActions()
        {
            var key = cargoShortcut?.Value.IsDown() == true ? Id + ":cargo"
                : inspectShortcut?.Value.IsDown() == true ? Id + ":inspect" : null;
            if (key == null) return;
            var action = ContextActionProvider.GetAvailableActions().FirstOrDefault(candidate => candidate.Key == key);
            if (action != null) ContextActionProvider.TryInvoke(action);
        }
    }
}
