using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using Manager.Save;
using Mirror;
using Sevo.Framework.Runtime;
using UI;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.CelestialLayout;

public sealed partial class Plugin
{
    private string? travelCharacter;
    private string? reloadCharacter;
    private Harmony? travelHarness;
    private EnterCommandMedbay? respawnMedbay;
    private PlayerHealth? respawnMedbayHealth;
    private ParentEntity? medbayExitParent;
    private bool observedMedbayExit;
    private PlayerHealth? initialRespawnHealth;
    private int initialRespawnFrame;
    private int medbayDepartureFrame;
    private float nextTravelStatus;
    private bool travelFinished;
    private bool saveRequested;
    private bool finishedSaveCheck;
    private bool reloadPickerPending;
    private bool reloadPickerOpened;
    private float reloadDeadline;
    private float saveDeadline;
    private float nextSaveCheck;

    private void ConfigureTravelFixture()
    {
        reloadCharacter = Environment.GetEnvironmentVariable("SEVO_KEEL_CELESTIAL_LAYOUT_RELOAD_CHARACTER");
        bool travel = Environment.GetEnvironmentVariable("SEVO_KEEL_CELESTIAL_LAYOUT_TRAVEL") == "1";
        if (!travel && string.IsNullOrEmpty(reloadCharacter)) return;
        try
        {
            string? session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
            string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            if (!Guid.TryParseExact(session, "N", out _) || string.IsNullOrWhiteSpace(state))
                throw new InvalidOperationException("The fixture requires a managed disposable session.");
            ProfileProtocol.Open(state, Paths.GameRootPath, session!);
            const string prefix = "ConnectorProbe-";
            string ownCharacter = prefix + session!.Substring(0, 8);
            if (!string.IsNullOrEmpty(reloadCharacter))
            {
                if (travel || reloadCharacter == ownCharacter || !reloadCharacter.StartsWith(prefix, StringComparison.Ordinal)
                    || reloadCharacter.Length != prefix.Length + 8
                    || !reloadCharacter.Skip(prefix.Length).All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    throw new InvalidOperationException("Reload requires an exact disposable character from a previous session.");
                reloadPickerPending = true;
                return;
            }
            travelCharacter = ownCharacter;
            travelHarness = new Harmony(Owner + ".disposable-travel");
            travelHarness.Patch(AccessTools.DeclaredMethod(typeof(PlayerHealth), nameof(PlayerHealth.OnRespawnFinished)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterInitialRespawn)));
            var medbayEnter = AccessTools.DeclaredMethod(typeof(EnterCommandMedbay), "OnEnter", new[] { typeof(PlayerCommandManager) })
                ?? throw new MissingMethodException("The stock medbay entry seam is missing.");
            travelHarness.Patch(medbayEnter, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMedbayEnter)));
            var medbayExit = AccessTools.DeclaredMethod(typeof(EnterCommandMedbay), "OnExit", new[] { typeof(PlayerCommandManager) })
                ?? throw new MissingMethodException("The stock medbay exit seam is missing.");
            travelHarness.Patch(medbayExit, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterMedbayExit)));
            var tutorial = AccessTools.DeclaredMethod(typeof(GameIntroductionResources.GameIntroductionLogic), "UpdateCreativeTutorial", Type.EmptyTypes)
                ?? throw new MissingMethodException("The stock Creative tutorial seam is missing.");
            travelHarness.Patch(tutorial, prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowCreativeTutorial)));
            GameloopManager.OnTransformJobFinished += BeginTravel;
        }
        catch (Exception exception)
        {
            travelCharacter = null;
            reloadCharacter = null;
            travelHarness?.UnpatchSelf();
            Logger.LogError("KEEL_CELESTIAL_FIXTURE FAIL " + exception);
        }
    }

    private void OnDestroy()
    {
        GameloopManager.OnTransformJobFinished -= BeginTravel;
        travelHarness?.UnpatchSelf();
    }

    private static bool AllowCreativeTutorial(GameIntroductionResources.GameIntroductionLogic __instance)
        => __instance.missionPhase != 0 || instance.travelCharacter == null
            || SaveManager.CharacterName != instance.travelCharacter;

    private bool IsFixturePlayer(PlayerCommandManager? player)
        => player != null && player.isLocalPlayer && ReferenceEquals(Dependency.PlayerManager, player)
            && travelCharacter != null && SaveManager.CharacterName == travelCharacter
            && GameIntroductionResources.IsStartingSystem(PlanetarySystem.CurrentStarData);

    private static void BeforeMedbayEnter(EnterCommandMedbay __instance, PlayerCommandManager __0)
    {
        if (!instance.IsFixturePlayer(__0) || __0.Health?.IsDead != true || !__0.Health.IsRespawning) return;
        instance.respawnMedbay = __instance;
        instance.respawnMedbayHealth = __0.Health;
        instance.medbayExitParent = null;
        instance.observedMedbayExit = false;
        instance.Logger.LogInfo($"KEEL_CELESTIAL_TRAVEL_PROBE observed stock respawn medbay={__instance.parentEntity?.SGUID}");
    }

    private static void AfterMedbayExit(EnterCommandMedbay __instance, PlayerCommandManager __0)
    {
        if (!instance.IsFixturePlayer(__0) || !ReferenceEquals(instance.respawnMedbay, __instance)
            || !ReferenceEquals(instance.respawnMedbayHealth, __0.Health)) return;
        instance.medbayExitParent = __0.LockedEntity?.Parent;
        instance.observedMedbayExit = true;
        instance.Logger.LogInfo($"KEEL_CELESTIAL_TRAVEL_PROBE observed stock medbay exit medbay={__instance.parentEntity?.SGUID}"
            + $" physicalParent={instance.medbayExitParent?.SGUID} transform={__0.LockedEntityTransformId}");
    }

    private static void AfterInitialRespawn(PlayerHealth __instance)
    {
        if (!instance.IsFixturePlayer(Dependency.PlayerManager)
            || !ReferenceEquals(Dependency.PlayerManager?.Health, __instance)
            || !ReferenceEquals(instance.respawnMedbayHealth, __instance))
            return;
        instance.initialRespawnHealth = __instance;
        instance.initialRespawnFrame = Time.frameCount;
        instance.Logger.LogInfo($"KEEL_CELESTIAL_TRAVEL_PROBE stock initial respawn completed frame={Time.frameCount}");
    }

    private void AdvanceReloadPicker()
    {
        if (!reloadPickerPending) return;
        try
        {
            if (reloadDeadline == 0f) reloadDeadline = Time.realtimeSinceStartup + 120f;
            if (Time.realtimeSinceStartup >= reloadDeadline)
                throw new TimeoutException("The stock character picker did not become ready.");
            if (stock == null || PlayerState.sessionMode != PlayerState.SessionMode.mainMenu || NetworkServer.active
                || NetworkManager.singleton == null) return;

            if (!reloadPickerOpened)
            {
                if (UI_TitleScreen.instance == null || UI_TitleScreen.IsActive?.Value != true) return;
                string path = GetIsolatedCharacterPath(reloadCharacter!);
                var saved = PlayerSave_mp.LoadFromDisk(path);
                if (saved == null || saved.characterName != reloadCharacter || saved.starData.seed != target.seed
                    || saved.gameMode != PlayerState.GameMode.creative)
                    throw new InvalidOperationException("The disposable character save does not match the celestial destination fixture.");
                UI_TitleScreen.instance.ShowPlayScreen();
                reloadPickerOpened = true;
                return;
            }

            var selection = UnityEngine.Object.FindAnyObjectByType<UI_CharacterSelection>();
            if (selection == null || UI_CharacterSelection.IsActive?.Value != true) return;
            string expected = GetIsolatedCharacterPath(reloadCharacter!);
            var file = AccessTools.DeclaredField(typeof(UI_CharacterSlot), "fileInfo")
                ?? throw new MissingFieldException(typeof(UI_CharacterSlot).FullName, "fileInfo");
            var slots = selection.GetComponentsInChildren<UI_CharacterSlot>(false)
                .Where(slot => file.GetValue(slot) is FileInfo candidate
                    && string.Equals(candidate.FullName, expected, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (slots.Length == 0) return;
            if (slots.Length > 1) throw new InvalidOperationException("More than one stock slot points to the saved test character.");
            reloadPickerPending = false;
            slots[0].PressButton();
            Logger.LogInfo($"KEEL_CELESTIAL_RELOAD_PROBE selected stock character={reloadCharacter}");
        }
        catch (Exception exception)
        {
            reloadPickerPending = false;
            reloadCharacter = null;
            Logger.LogError("KEEL_CELESTIAL_RELOAD_PROBE FAIL " + exception);
        }
    }

    private static string GetIsolatedCharacterPath(string character)
    {
        string path = Path.GetFullPath(PlayerSave_mp.GetPath(character));
        string root = Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native character file is outside the isolated Unity profile.");
        for (string? entry = path; entry != null && !string.Equals(entry, root, StringComparison.OrdinalIgnoreCase);
            entry = Path.GetDirectoryName(entry))
        {
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The native character path traverses a redirected entry.");
        }
        return path;
    }

    private static bool NativeSoloReady()
        => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && !PlayerState.InLoadingScreen && !PlayerState.InMenu && !PlayerState.IsQuittingToDesktopOrTitle
            && !PlanetarySystem.InSystemTransition && PlayerState.sessionMode == PlayerState.SessionMode.solo
            && PlayerState.gameMode == PlayerState.GameMode.creative && NetworkServer.active && NetworkServer.dontListen;

    private async void BeginTravel()
    {
        if (travelCharacter == null || !acceptedLayout) return;
        var player = Dependency.PlayerManager;
        ReportTravelGate(player);
        if (!NativeSoloReady()) return;
        if (!IsFixturePlayer(player) || player!.Health == null || !observedMedbayExit
            || !ReferenceEquals(player.Health, initialRespawnHealth) || Time.frameCount <= initialRespawnFrame
            || player.Health.IsRespawning || player.Health.IsDead || respawnMedbay?.inAnimation != false || PlayerState.SystemEntered
            || Time.frameCount <= medbayDepartureFrame) return;
        try
        {
            if (SaveManager.CharacterName != travelCharacter || !GameIntroductionResources.IsStartingSystem(PlanetarySystem.CurrentStarData))
                throw new InvalidOperationException("Travel requires this session's starting character.");
            if (player.SkyTransform.Data.parentId != 0)
            {
                MoveClearOfMedbay(player);
                return;
            }
            GameloopManager.OnTransformJobFinished -= BeginTravel;
            var catalog = SolarBodyGenerator.GetStarSystemBodiesData(target);
            VerifyCatalog(catalog);
            var planet = catalog[stock!.Count + 1];
            var arrival = planet.systemPosition + new Vector3l(Vector3.up * (4f * planet.radius));
            Logger.LogInfo($"KEEL_CELESTIAL_TRAVEL_PROBE entering system={target.seed} character={travelCharacter}");
            SaveManager.UpdateCurrentSystemInfoDate();
            LoadManager.EnableLoadingScreen();
            var transition = PlanetarySystem.instance.NewSystem(target, Vector3l.zero);
            player.SkyTransform.InitializeTransform(arrival, Quaternion.identity, target);
            PositionManager.ComputeFloatingOrigin();
            await transition;
            travelFinished = true;
        }
        catch (Exception exception)
        {
            GameloopManager.OnTransformJobFinished -= BeginTravel;
            Logger.LogError("KEEL_CELESTIAL_TRAVEL_PROBE FAIL " + exception);
        }
    }

    private void ReportTravelGate(PlayerCommandManager? player)
    {
        if (initialRespawnHealth == null || Time.realtimeSinceStartup < nextTravelStatus) return;
        nextTravelStatus = Time.realtimeSinceStartup + 5f;
        bool nativeSafe = GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe;
        int parentId = nativeSafe && player != null ? player.SkyTransform.Data.parentId : -1;
        Logger.LogInfo($"KEEL_CELESTIAL_TRAVEL_PROBE waiting frame={Time.frameCount} nativeSafe={nativeSafe} parentId={parentId}"
            + $" relocated={medbayDepartureFrame != 0} menu={PlayerState.InMenu} loading={PlayerState.InLoadingScreen}"
            + $" transition={PlanetarySystem.InSystemTransition} local={player?.isLocalPlayer} respawning={player?.Health?.IsRespawning}"
            + $" dead={player?.Health?.IsDead} commandEntered={PlayerState.SystemEntered} medbayExited={observedMedbayExit}"
            + $" medbayAnimation={respawnMedbay?.inAnimation} lockedTransform={player?.LockedEntityTransformId}"
            + $" parent={player?.LockedEntity?.Parent?.SGUID} exitParent={medbayExitParent?.SGUID}");
    }

    private void MoveClearOfMedbay(PlayerCommandManager player)
    {
        if (medbayDepartureFrame != 0) return;
        var locked = player.LockedEntity;
        var parent = locked?.Parent;
        var medbay = respawnMedbay?.parentEntity;
        if (locked == null || parent == null || !parent.IsInitialized || parent.WillBeUnspawned
            || locked.SkyTransform?.id != player.SkyTransform.Data.parentId
            || !(ReferenceEquals(parent, medbayExitParent)
                || (medbay != null && (IsDockedWithin(parent, medbay) || IsDockedWithin(medbay, parent)))))
            throw new InvalidOperationException($"The remaining parent is outside the observed stock medbay exit"
                + $" parent={parent?.SGUID} medbay={medbay?.SGUID} exitParent={medbayExitParent?.SGUID}"
                + $" playerTransform={player.SkyTransform.Data.parentId} lockedTransform={locked?.SkyTransform?.id}.");
        var entered = new List<BrickCoord>();
        player.MultiTool.ActionActivate.SetEnteredCommand(entered);
        if (entered.Count != 0) return;

        float radius = parent.ParentRadius;
        if (!(radius > 0f) || !float.IsFinite(radius) || radius > 10000f)
            throw new InvalidOperationException("The starting medbay bounds are unavailable.");
        var nearby = BlockEntityManager.Entities.Where(entity => entity != null && entity.IsInitialized && !entity.WillBeUnspawned
            && entity.SkyTransform.CurrentSystem == PlanetarySystem.CurrentStarData).ToArray();
        foreach (var direction in new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
        {
            var position = parent.ParentSystemPosition + new Vector3l(direction * (radius + 250f));
            if (PlanetarySystem.SolarBodies.Any(body => body.IsShipTooClose(position))
                || nearby.Any(entity => !((position - entity.ParentSystemPosition).Magnitude > entity.ParentRadius + 25f)))
                continue;
            player.SkyTransform.SetTransform(position, player.TransformData.rotation);
            medbayDepartureFrame = Time.frameCount;
            Logger.LogInfo($"KEEL_CELESTIAL_TRAVEL_PROBE moved beyond the exited medbay bounds frame={medbayDepartureFrame}");
            return;
        }
        throw new InvalidOperationException("No clear test position was found outside the starting medbay.");
    }

    private static bool IsDockedWithin(ParentEntity child, ParentEntity ancestor)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            if (ReferenceEquals(child, ancestor)) return true;
            if (child.Rigidbody?.isParented != true || child.Rigidbody.dockedParent?.Parent is not { } parent) return false;
            child = parent;
        }
        return false;
    }

    private void CheckDestinationSave()
    {
        if (!acceptedDestination || finishedSaveCheck) return;
        if (!string.IsNullOrEmpty(reloadCharacter))
        {
            finishedSaveCheck = true;
            if (SaveManager.CharacterName == reloadCharacter)
                Logger.LogInfo($"KEEL_CELESTIAL_RELOAD_PROBE PASS system={target.seed} character={reloadCharacter} layout={destinationSignature}");
            else Logger.LogError("KEEL_CELESTIAL_RELOAD_PROBE FAIL unexpected character");
            return;
        }
        if (!travelFinished || travelCharacter == null || !NativeSoloReady() || GameloopManager.jobStarted
            || Time.realtimeSinceStartup < nextSaveCheck) return;

        nextSaveCheck = Time.realtimeSinceStartup + 0.25f;
        try
        {
            if (SaveManager.CharacterName != travelCharacter || PlanetarySystem.CurrentStarData.seed != target.seed)
                throw new InvalidOperationException("The disposable character left the tested destination.");
            string path = GetIsolatedCharacterPath(travelCharacter);
            if (!saveRequested)
            {
                SaveManager.SaveOnSceneQuit();
                saveRequested = true;
                saveDeadline = Time.realtimeSinceStartup + 30f;
                return;
            }
            if (Time.realtimeSinceStartup >= saveDeadline)
                throw new InvalidOperationException("The native destination save did not appear.");
            if (PlayerSave_mp.LoadFromDisk(path)?.starData.seed != target.seed) return;
            finishedSaveCheck = true;
            Logger.LogInfo($"KEEL_CELESTIAL_SAVE_PROBE PASS system={target.seed} character={travelCharacter} layout={destinationSignature}");
        }
        catch (Exception exception)
        {
            finishedSaveCheck = true;
            Logger.LogError("KEEL_CELESTIAL_SAVE_PROBE FAIL " + exception);
        }
    }
}
