using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.SublightControlProbe
{
    [BepInPlugin(Id, "Keel Sublight Control Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.sublightprobe";
        private const string Prefix = "KEEL_SUBLIGHT_CONTROL_PROBE";
        private const float VelocityThreshold = 0.25f;
        private enum Stage { Off, Select, Enter, Exit, Ready, Pulse, Expire, ArmReentry, Reenter, VerifyRevoked, Done }
        private Stage stage;
        private ParentEntity? grid;
        private SublightControlLease? lease;
        private KeyboardShortcut selectKey;
        private KeyboardShortcut pulseKey;
        private KeyboardShortcut reentryKey;
        private Vector3 forward;
        private float startVelocity;
        private float deadline;
        private int reentryFrame;
        private string session = "";

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE") != "1") return;
            try
            {
                ValidateProfile();
                if (PrepareAutomatic()) return;
                selectKey = Config.Bind("Probe", "SelectGrid", new KeyboardShortcut(KeyCode.F6),
                    "Select a newly created disposable ship under the stock build crosshair.").Value;
                pulseKey = Config.Bind("Probe", "ForwardPulse", new KeyboardShortcut(KeyCode.F7),
                    "After normal pilot exit settles, test a bounded stock sublight input.").Value;
                reentryKey = Config.Bind("Probe", "ArmStockReentry", new KeyboardShortcut(KeyCode.F8),
                    "Arm neutral input while the operator enters the stock pilot controls.").Value;
                stage = Stage.Select;
                Report("ready for ship selection with F6");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void ValidateProfile()
        {
            var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
            string? game = Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_GAME");
            string? company = Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_COMPANY");
            string? product = Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_PRODUCT");
            Require(Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                && Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                && !string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(product)
                && company != "Moonfire Entertainment" && product != "Starship EVO"
                && Application.companyName == company && Application.productName == product
                && string.Equals(Path.GetFullPath(game!).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                "Use the exact isolated lab path, product and managed experimental profile.");
            ProfileProtocol.Open(state!, Paths.GameRootPath, session);
        }

        private void Update()
        {
            if (automatic)
            {
                UpdateAutomatic();
                return;
            }
            if (stage == Stage.Off || stage == Stage.Done) return;
            if (PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle || PlayerState.InMenu)
            {
                if (lease != null) Fail(new InvalidOperationException("The world or gameplay focus changed while the probe held control."));
                return;
            }
            if (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.gameMode != PlayerState.GameMode.creative
                || !NetworkServer.active || !NetworkServer.dontListen || Dependency.PlayerManager == null
                || !Dependency.PlayerManager.IsLocal || !SublightControlService.IsAvailable) return;

            try
            {
                if (stage == Stage.Select)
                {
                    if (selectKey.IsDown()) SelectGrid();
                    return;
                }
                Require(grid != null && grid.IsInitialized && !grid.WillBeUnspawned && !grid.isDestroyed
                    && BlockEntityManager.instance != null
                    && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(grid.SGUID), grid), "Selected ship unloaded.");
                Step(grid!);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void SelectGrid()
        {
            var player = Dependency.PlayerManager;
            if (!player.PlayerActions.RaycastFromPlayerPerspective(8f, out var hit)
                || hit.Entity == null || hit.Entity.Parent is not { } target || target.Type != EntityType.spaceship)
            {
                Report("aim at the disposable ship within 8 metres and press F6 again");
                return;
            }
            Require(target.controlMode == BaseVehicle.VehicleControlMode.notControlled
                && !ReferenceEquals(player.LastSpaceship, target), "Select a ship the local player has not piloted this session.");
            Reject(target, "before stock piloting");
            grid = target;
            stage = Stage.Enter;
            Report($"selected ship={target.SGUID}. Use stock pilot controls, then exit.");
        }

        private void Step(ParentEntity target)
        {
            bool unoccupied = target.controlMode == BaseVehicle.VehicleControlMode.notControlled;
            float now = Time.unscaledTime;
            switch (stage)
            {
                case Stage.Enter:
                    if (target.controlMode != BaseVehicle.VehicleControlMode.localPlayer) return;
                    Reject(target, "during stock piloting");
                    stage = Stage.Exit;
                    break;
                case Stage.Exit:
                    if (!unoccupied) return;
                    Reject(target, "during stock post-exit control");
                    stage = Stage.Ready;
                    Report("wait for native exit damping, then press F7 for the forward pulse");
                    break;
                case Stage.Ready:
                    if (!pulseKey.IsDown() || !Acquire(target)) return;
                    var ship = target.GetComponent<Spaceship>();
                    forward = ship.transform.rotation * ship.Orientation * Vector3.forward;
                    startVelocity = Vector3.Dot(target.Rigidbody.LinearVelocity, forward);
                    Require(!SublightControlService.TryAcquire(Id + ".second", target, out _), "A duplicate lease was accepted.");
                    deadline = now + 1.25f;
                    stage = Stage.Pulse;
                    Submit(new Vector3(0f, 0f, 0.35f));
                    break;
                case Stage.Pulse:
                    Require(unoccupied, "A pilot entered before the pulse finished.");
                    float delta = Vector3.Dot(target.Rigidbody.LinearVelocity, forward) - startVelocity;
                    Require(float.IsFinite(delta), "Native velocity sample was nonfinite.");
                    if (delta < VelocityThreshold && now < deadline)
                    {
                        Submit(new Vector3(0f, 0f, 0.35f));
                        return;
                    }
                    Require(delta >= VelocityThreshold, $"Forward velocity changed by only {delta:F3} m/s.");
                    Report($"forward pulse PASS delta={delta:F3} m/s");
                    deadline = now + 0.55f;
                    stage = Stage.Expire;
                    break;
                case Stage.Expire:
                    if (now < deadline) return;
                    Require(!lease!.TrySubmit(Vector3.zero, Vector3.zero), "Timed-out input was accepted.");
                    Release();
                    Report("expiration PASS. Press F8, then enter stock pilot controls within 15 seconds.");
                    stage = Stage.ArmReentry;
                    break;
                case Stage.ArmReentry:
                    if (!reentryKey.IsDown() || !Acquire(target)) return;
                    deadline = now + 15f;
                    stage = Stage.Reenter;
                    Submit(Vector3.zero);
                    break;
                case Stage.Reenter:
                    Require(now < deadline, "Stock re-entry timed out.");
                    if (target.controlMode == BaseVehicle.VehicleControlMode.localPlayer)
                    {
                        reentryFrame = Time.frameCount;
                        stage = Stage.VerifyRevoked;
                        return;
                    }
                    Require(unoccupied, "Unexpected controller took the ship.");
                    if (!lease!.TrySubmit(Vector3.zero, Vector3.zero))
                    {
                        // A queued native entry may revoke control one frame before stock mode changes.
                        reentryFrame = Time.frameCount;
                        stage = Stage.VerifyRevoked;
                    }
                    break;
                case Stage.VerifyRevoked:
                    if (Time.frameCount <= reentryFrame + 1) return;
                    Require(target.controlMode == BaseVehicle.VehicleControlMode.localPlayer,
                        "The operator did not enter stock pilot control.");
                    Require(!lease!.TrySubmit(Vector3.zero, Vector3.zero), "The old lease still accepted input after stock re-entry.");
                    Release();
                    Report("PASS pre-pilot rejection, occupied rejection, native exit arbitration, exclusive lease, measurable forward motion, expiration and stock re-entry");
                    stage = Stage.Done;
                    break;
            }
        }

        private bool Acquire(ParentEntity target)
        {
            if (SublightControlService.TryAcquire(Id, target, out lease)) return true;
            Report("ship is not eligible yet. Verify normal exit completed and the ship is powered, owned and free.");
            return false;
        }

        private void Submit(Vector3 translation) =>
            Require(lease?.TrySubmit(translation, Vector3.zero) == true, "The active lease rejected input.");

        private void Reject(ParentEntity target, string phase)
        {
            bool accepted = SublightControlService.TryAcquire(Id, target, out var rejected);
            rejected?.Dispose();
            Require(!accepted, "Unexpected acquisition " + phase + ".");
            Report("rejection PASS " + phase);
        }

        private void Release()
        {
            lease?.Dispose();
            lease = null;
        }

        private void OnDestroy()
        {
            Release();
            waypointLease?.Dispose();
            ReleaseCombinedCamera();
        }
        private void Report(string message) => Logger.LogInfo($"{Prefix} session={session} stage={stage} {message}");

        private void Fail(Exception exception)
        {
            Release();
            Logger.LogError($"{Prefix} FAIL session={session} stage={stage} {exception.GetBaseException().Message}");
            stage = Stage.Done;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
