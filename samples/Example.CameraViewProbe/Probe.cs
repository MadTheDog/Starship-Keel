using System;
using System.Collections;
using System.Diagnostics;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.CameraViewProbe
{
    [BepInPlugin(Id, "Keel Camera View Acceptance Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.camera-view-probe";
        private bool pending;
        private static Probe? observer;
        private PlayerActions? actions;
        private int ordinaryUpdates;
        private int interactionUpdates;
        private int releaseUpdates;
        private string session = "";
        private string phase = "";

        private void Awake() => pending = Environment.GetEnvironmentVariable("SEVO_KEEL_CAMERA_VIEW_PROBE") == "1";

        private void Update()
        {
            if (!pending) return;
            pending = false;
            StartCoroutine(Report());
        }

        private IEnumerator Report()
        {
            var run = Run();
            try
            {
                while (true)
                {
                    object? wait;
                    try
                    {
                        if (!run.MoveNext()) yield break;
                        wait = run.Current;
                    }
                    catch (Exception exception)
                    {
                        Logger.LogError($"KEEL_CAMERA_VIEW FAIL phase={phase} session={session} {exception.Message}");
                        yield break;
                    }
                    yield return wait;
                }
            }
            finally { (run as IDisposable)?.Dispose(); }
        }

        private IEnumerator Run()
        {
            phase = "profile";
            session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
            string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            Require(Guid.TryParseExact(session, "N", out _) && state != null
                && Application.companyName == "SEVO Keel Conn Lab 010" && Application.productName == "SEVO CT Test",
                "The exact managed connector lab is required.");
            ProfileProtocol.Open(state!, Paths.GameRootPath, session);

            var clock = Stopwatch.StartNew();
            phase = "waiting-for-stock-world";
            while (!Ready())
            {
                Deadline(clock);
                yield return null;
            }

            var player = Dependency.PlayerManager;
            var cameraAnchor = player.CameraTransform.parent ?? throw new InvalidOperationException("Stock camera anchor is missing.");
            Require(cameraAnchor.parent == player.transform,
                "Stock camera was not parented to the physical player.");
            var physicalParent = player.LockedEntity;
            var physicalPosition = player.SkyTransform.SystemPosition;
            var targetPose = new WorldTransform(physicalPosition + new Vector3l(new Vector3(25f, 0f, 0f)), Quaternion.identity);
            ParentEntity? target = null;
            IDisposable? lease = null;
            var harmony = new Harmony(Id);
            try
            {
                actions = player.PlayerActions;
                observer = this;
                PatchObserver(harmony, "UpdateBlueprintEntityPlacement", nameof(ObserveOrdinaryUpdate));
                PatchObserver(harmony, "ReleaseCapturedItemInputs", nameof(ObserveReleaseUpdate));
                var activate = AccessTools.DeclaredMethod(typeof(ActionActivate), nameof(ActionActivate.TryActivate),
                    new[] { typeof(VoxelRaycastHit) });
                Require(activate != null, "The stock interaction observation seam was unavailable.");
                harmony.Patch(activate, prefix: new HarmonyMethod(typeof(Probe), nameof(ObserveInteractionUpdate)));

                phase = "creating-owned-target";
                target = player.NetworkSetup.ServerCreateEntity(TabHull.HULL_A.PreviewBrickData, targetPose, EntityType.spaceship);
                Require(target != null, "The stock builder did not create the target.");
                while (!Ready() || !target!.IsInitialized || ordinaryUpdates == 0 || interactionUpdates == 0)
                {
                    Deadline(clock);
                    yield return null;
                }

                Vector3 offset = new Vector3(0f, 0f, -4f);
                phase = "following-and-suppressing";
                Require(CameraViewService.TryFollow(target!, offset, Quaternion.identity, out lease) && lease != null,
                    "Keel did not admit the local stock view lease.");
                int ordinaryBefore = ordinaryUpdates;
                int interactionBefore = interactionUpdates;
                int releaseBefore = releaseUpdates;
                while (releaseUpdates < releaseBefore + 3)
                {
                    Deadline(clock);
                    yield return null;
                }
                Require(ordinaryUpdates == ordinaryBefore && interactionUpdates == interactionBefore
                    && cameraAnchor.parent != player.transform
                    && Vector3.Distance(cameraAnchor.position, target!.TransformData.LocalToWorld(offset)) < 0.5f,
                    "Stock placement ran or the camera did not follow the target.");
                Require(ReferenceEquals(player.LockedEntity, physicalParent)
                    && ((Vector3)(player.SkyTransform.SystemPosition - physicalPosition).ToFloat3()).magnitude < 5f,
                    "The view changed the physical player or its parent.");

                while (!Ready()) { Deadline(clock); yield return null; }
                var moved = target!.SkyTransform.SystemPosition + new Vector3l(new Vector3(0f, 3f, 6f));
                target.SkyTransform.SetTransform(moved, Quaternion.identity);
                int followTick = releaseUpdates;
                while (releaseUpdates < followTick + 2) { Deadline(clock); yield return null; }
                Require(Vector3.Distance(cameraAnchor.position, target.TransformData.LocalToWorld(offset)) < 0.5f
                    && ordinaryUpdates == ordinaryBefore && interactionUpdates == interactionBefore, "The active view lost the moved target or permitted stock placement.");
                Logger.LogInfo($"KEEL_CAMERA_VIEW FOLLOW PASS stockActionsSuppressed=True physicalPlayerRetained=True session={session}");

                phase = "restoring-on-dispose";
                lease!.Dispose();
                while (ordinaryUpdates == ordinaryBefore || cameraAnchor.parent != player.transform)
                {
                    Deadline(clock);
                    yield return null;
                }
                Require(cameraAnchor.localPosition == Vector3.zero && cameraAnchor.localRotation == Quaternion.identity,
                    "Stock player anchor was not reset to its neutral local pose.");

                phase = "restoring-on-target-destruction";
                while (!Ready()) { Deadline(clock); yield return null; }
                Require(CameraViewService.TryFollow(target, offset, Quaternion.identity, out lease) && lease != null,
                    "The acknowledged restore did not release the lease slot.");
                while (!Ready()) { Deadline(clock); yield return null; }
                int beforeDestroy = ordinaryUpdates;
                Dependency.PlayerNetwork.ServerDestroyEntity(target);
                while (ordinaryUpdates == beforeDestroy || cameraAnchor.parent != player.transform)
                {
                    Deadline(clock);
                    yield return null;
                }
                Require(target == null || target.isDestroyed || target.WillBeUnspawned
                    || !ReferenceEquals(target.ID.Entity, target),
                    "The native target was still live after destruction.");
                Logger.LogInfo($"KEEL_CAMERA_VIEW PASS disposeRestored=True targetLossRestored=True session={session}");
            }
            finally
            {
                lease?.Dispose();
                observer = null;
                harmony.UnpatchSelf();
                if (Ready() && target != null && !target.WillBeUnspawned)
                    Dependency.PlayerNetwork.ServerDestroyEntity(target);
            }
        }

        private static bool Ready()
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                && !GameloopManager.jobStarted && PlayerState.sessionMode == PlayerState.SessionMode.solo
                && NetworkServer.active && NetworkServer.dontListen && !PlayerState.InLoadingScreen
                && !PlayerState.SystemEntered && !PlayerState.InMenu && !PlayerState.IsQuittingToDesktopOrTitle
                && Dependency.PlayerManager != null && Dependency.PlayerManager.IsLocal
                && !Dependency.PlayerManager.Health.IsRespawning && !Dependency.PlayerManager.IsSeated;

        private static void PatchObserver(Harmony harmony, string stockName, string hookName)
        {
            var method = AccessTools.DeclaredMethod(typeof(PlayerActions), stockName, Type.EmptyTypes);
            Require(method != null, "A stock action observation seam was unavailable.");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Probe), hookName));
        }

        private static void ObserveOrdinaryUpdate(PlayerActions __instance)
        {
            if (observer is { } probe && ReferenceEquals(probe.actions, __instance)) probe.ordinaryUpdates++;
        }

        private static void ObserveInteractionUpdate(ActionActivate __instance)
        {
            if (observer is { } probe && Dependency.PlayerManager != null
                && ReferenceEquals(Dependency.PlayerManager.MultiTool.ActionActivate, __instance))
                probe.interactionUpdates++;
        }

        private static void ObserveReleaseUpdate(PlayerActions __instance)
        {
            if (observer is { } probe && ReferenceEquals(probe.actions, __instance)) probe.releaseUpdates++;
        }

        private void Deadline(Stopwatch clock)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException(phase);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
