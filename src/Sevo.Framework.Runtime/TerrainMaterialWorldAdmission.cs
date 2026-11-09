using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Manager;

namespace Sevo.Framework.Runtime
{
    internal static class TerrainMaterialWorldAdmission
    {
        private static ProfileSession? profile;

        internal static string? Install(Harmony harmony, Assembly game, ProfileSession session)
        {
            profile = session;
            var missing = new List<string>();
            var entries = new[]
            {
                ("UI.UI_CharacterSlot", "PressButton", nameof(BeforeLocalEntry)),
                ("UI.UI_CharacterCreation", "PlayWithNewCharacter", nameof(BeforeLocalEntry)),
                ("UI.UI_LobbySlot", "ConnectToLobby", nameof(BeforeRemoteEntry)),
                ("UI.MainScreen.UI_MultiplayerLobby", "ToggleLobby", nameof(BeforeLobbyToggle))
            };
            foreach (var (owner, name, prefix) in entries)
            {
                var failure = TryPatch(harmony, game, owner, name, prefix);
                if (failure != null) missing.Add(failure);
            }

            if (missing.Count == 0) return null;
            var fallback = TryPatch(harmony, game, "UI.UI_TitleScreen", "ShowPlayScreen", nameof(BeforeLocalEntry));
            if (fallback != null) missing.Add(fallback);
            return "World generation entry guards are incomplete. " + string.Join(" | ", missing);
        }

        internal static void Disable() => profile = null;

        private static string? TryPatch(Harmony harmony, Assembly game, string owner, string name, string prefix)
        {
            try
            {
                var type = game.GetType(owner);
                var target = type == null ? null : AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
                if (target == null || target.IsStatic || target.ReturnType != typeof(void))
                    return "Missing stock entry: " + owner + "." + name;
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(TerrainMaterialWorldAdmission), prefix)
                    { priority = Priority.First });
                return null;
            }
            catch (Exception exception)
            {
                return owner + "." + name + ": " + exception.Message;
            }
        }

        private static bool BeforeLocalEntry() => Check(false);
        private static bool BeforeRemoteEntry() => Check(true);

        private static bool BeforeLobbyToggle()
        {
            if (SteamLobbyManager.IsLobbyOpen) return true;
            if (profile != null && (CelestialLayoutProvider.IsAvailable || BodyEnvironmentProvider.IsAvailable
                || TerrainPlacement.IsAvailable || TerrainGenerationProvider.PlanetAvailable
                || TerrainGenerationProvider.AsteroidAvailable))
                return Reject("The managed world generation providers are enabled for solo play.");
            return Check(true);
        }

        private static bool Check(bool remoteLobby)
        {
            var reason = TerrainMaterialRegistration.GetWorldAdmissionFailure(profile, remoteLobby);
            return reason == null || Reject(reason);
        }

        private static bool Reject(string reason)
        {
            UI.UI_ConfirmationWindow.OpenWindow(reason);
            return false;
        }
    }
}
