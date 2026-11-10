using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using UI;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Adds mod-owned service actions to unused slots in the station hub action row.</summary>
    public static class StationServices
    {
        private static readonly SortedDictionary<string, (string Label, Func<ParentEntity, bool> Matches, Action<ParentEntity> Invoke)> Services =
            new SortedDictionary<string, (string, Func<ParentEntity, bool>, Action<ParentEntity>)>(StringComparer.Ordinal);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static GenericButtonData? button3;
        private static GenericButtonData? button4;
        private static Action<string>? warning;
        private static int gameThread;
        private static bool sealedRegistration;

        public static bool IsAvailable { get; private set; }

        public static void Register(string ownerId, string localName, string label, Func<ParentEntity, bool> matches,
            Action<ParentEntity> invoke)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (sealedRegistration) throw new InvalidOperationException("Station service registration is closed.");
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A service label is required.", nameof(label));
            Services.Add(key, (label, matches ?? throw new ArgumentNullException(nameof(matches)),
                invoke ?? throw new ArgumentNullException(nameof(invoke))));
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var open = AccessTools.DeclaredMethod(typeof(UI_StationLandingPage), nameof(UI_StationLandingPage.OpenHubUI));
            harmony.Patch(open, transpiler: new HarmonyMethod(typeof(StationServices), nameof(ReplaceClear)));
            var close = AccessTools.DeclaredMethod(typeof(UI_StationLandingPage), "CloseUI");
            harmony.Patch(close, postfix: new HarmonyMethod(typeof(StationServices), nameof(RemoveButtons)));
            warning = reportWarning;
            gameThread = Environment.CurrentManagedThreadId;
            IsAvailable = true;
        }

        internal static void Disable()
        {
            IsAvailable = false;
            RemoveButtons();
        }

        private static IEnumerable<CodeInstruction> ReplaceClear(IEnumerable<CodeInstruction> instructions)
        {
            var clear = AccessTools.DeclaredMethod(typeof(UI_KioskGeneric), nameof(UI_KioskGeneric.ClearUI));
            var code = instructions.ToList();
            code.Single(instruction => instruction.Calls(clear)).operand =
                AccessTools.DeclaredMethod(typeof(StationServices), nameof(PrepareAndClear));
            return code;
        }

        private static void PrepareAndClear()
        {
            RemoveButtons();
            var station = UI_StationLandingPage.station;
            var row = UI_StationLandingPage.UI_ActionButtons;
            if (CanUse(station) && !row.skip && Services.Count > 0)
            {
                sealedRegistration = true;
                foreach (var service in Services)
                {
                    if (row.buttonData3 != null && row.buttonData4 != null) break;
                    try
                    {
                        if (!service.Value.Matches(station)) continue;
                        var button = new GenericButtonData
                        {
                            label = service.Value.Label,
                            onButtonPressed = () => Invoke(service.Key, station, service.Value.Invoke)
                        };
                        if (row.buttonData3 == null) row.buttonData3 = button3 = button;
                        else row.buttonData4 = button4 = button;
                    }
                    catch (Exception exception) { Report(service.Key, exception); }
                }
            }
            UI_KioskGeneric.ClearUI();
        }

        private static bool CanUse(ParentEntity station) => IsAvailable && Environment.CurrentManagedThreadId == gameThread
            && PlayerState.sessionMode == PlayerState.SessionMode.solo && station != null && !station.WillBeUnspawned
            && station.EntityStation != null;

        private static void Invoke(string key, ParentEntity station, Action<ParentEntity> action)
        {
            if (!CanUse(station) || UI_StationLandingPage.station != station || !UI_KioskGeneric.IsActive.Value) return;
            try { action(station); }
            catch (Exception exception) { Report(key, exception); }
        }

        private static void RemoveButtons()
        {
            if (button3 == null && button4 == null) return;
            var row = UI_StationLandingPage.UI_ActionButtons;
            if (ReferenceEquals(row.buttonData3, button3)) row.buttonData3 = null;
            if (ReferenceEquals(row.buttonData4, button4)) row.buttonData4 = null;
            button3 = button4 = null;
        }

        private static void Report(string key, Exception exception)
        {
            if (Reported.Add(key)) warning?.Invoke($"Station service {key} skipped: {exception.Message}");
        }
    }
}
