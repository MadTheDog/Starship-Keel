using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UI;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.NavigationProbe
{
    [BepInPlugin(Id, "Keel Native Navigation Fixture", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class NativeNavigationFixture : BaseUnityPlugin
    {
        private const string Id = "example.keel.nativenavigationfixture";
        private const string MarkerKey = Id + ":moving";
        private static readonly Color32 MarkerColor = new Color32(43, 179, 233, 255);
        private static NativeNavigationFixture? active;
        private Harmony? observer;
        private string session = "", sourceName = "";
        private ParentEntity? source, contact;
        private IDisposable? marker;
        private UI_WaypointWidget? widget;
        private Vector3 localPoint;
        private Vector3l firstPoint;
        private bool displayed, moved, selected, disposed, done;
        private float deadline;
        private bool? lateUpdateUnsafe;

        private void Awake()
        {
            const string flag = "SEVO_KEEL_NAVIGATION_FIXTURE";
            if (Environment.GetEnvironmentVariable(flag) != "1") return;
            try
            {
                session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string? game = Environment.GetEnvironmentVariable(flag + "_GAME");
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game) && Path.IsPathFullyQualified(game)
                    && Application.companyName == "SEVO Keel Conn Lab 010" && Application.productName == "SEVO CT Test"
                    && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "The fixture requires the exact managed disposable connector lab.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, session);
                Require(Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_NAVIGATION_MARKERS") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_NAVIGATION_TARGETS") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_AUTOMATIC") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_WAYPOINT") == "1",
                    "Enable both navigation capabilities and the existing isolated Sublight waypoint fixture.");
                sourceName = "Keel Sublight Probe " + session.Substring(0, 8);
                observer = new Harmony(Id);
                observer.Patch(AccessTools.Method(typeof(UI_WaypointWidget), nameof(UI_WaypointWidget.OnUpdate),
                    new[] { typeof(UI_MainWaypoints.WaypointData), typeof(Camera), typeof(Vector3) }),
                    postfix: new HarmonyMethod(typeof(NativeNavigationFixture), nameof(ObserveWidget)));
                observer.Patch(AccessTools.DeclaredMethod(typeof(UI_MainWaypoints), nameof(UI_MainWaypoints.LateUpdate)),
                    postfix: new HarmonyMethod(typeof(NativeNavigationFixture), nameof(ObserveHudBoundary))
                    { priority = Priority.Last });
                active = this;
                Record("armed", "awaiting the session-owned stock pilot and contact");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void Update()
        {
            if (!ReferenceEquals(active, this) || done) return;
            try
            {
                if (deadline == 0f) deadline = Time.realtimeSinceStartup + 180f;
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(
                    $"navigation evidence displayed={displayed} moved={moved} selected={selected} disposed={disposed}");
                if (!Ready()) return;
                if (disposed)
                {
                    if (widget != null || NavigationMarkerProvider.ReadMarkers().Any(value => value.Key == MarkerKey)) return;
                    done = true;
                    Record("pass", "stock HUD marker moved and was removed; live stock tracking selected the exact contact");
                    return;
                }

                var ship = HUD_TargetManager.Spaceship;
                var controlled = HUD_TargetManager.PlayerEntity;
                if (!HUD_TargetManager.IsActive.Value || ship == null || controlled == null || !ship.IsControlledByLocalPlayer
                    || !ReferenceEquals(ship.entity, controlled) || !Owned(controlled, sourceName))
                    return;
                if (source == null)
                {
                    var camera = Dependency.RenderPipeline?.playerCamera;
                    if (camera == null) return;
                    localPoint = controlled.TransformData.WorldToLocal(camera.transform.position + camera.transform.forward * 45f);
                    if (!NavigationMarkerProvider.TryAdd(Id, "moving", "Fixture moving navigation marker", controlled,
                        localPoint, MarkerColor, true, out marker))
                        throw new InvalidOperationException("Keel rejected the exact moving grid anchor.");
                    source = controlled;
                    Require(NavigationMarkerProvider.ReadMarkers().Any(value => value.Key == MarkerKey
                        && value.AnchorEntityId == controlled.ID && value.LocalPosition == localPoint),
                        "The owned marker was not present in Keel's registry.");
                    Record("marker-added", $"grid={source.SGUID};transform={source.transformId}");
                }
                Require(ReferenceEquals(source, controlled), "The stock controlled grid changed during the fixture.");
                if (!selected)
                {
                    if (contact == null)
                        foreach (var grid in SkyRigidbody.Entities)
                            if (grid != null && Owned(grid, sourceName + " Target")) { contact = grid; break; }
                    if (contact != null && Tracked(contact) && NavigationTargetService.TrySelectEntity(contact))
                    {
                        Require(HUD_TargetManager.CurrentTargetId == contact.TrackingDataId
                            && NavigationTargetService.TryGetSelectedEntity(out var read) && ReferenceEquals(read, contact),
                            "Stock HUD selected a different target.");
                        selected = true;
                        Record("native-target-selected", $"grid={contact.SGUID};tracking={contact.TrackingDataId}");
                    }
                }
                if (!displayed || !moved || !selected) return;
                marker!.Dispose();
                marker = null;
                disposed = true;
                deadline = Time.realtimeSinceStartup + 8f;
                Record("marker-disposed", "waiting for the native widget to be destroyed");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void ObserveHudBoundary(UI_MainWaypoints __instance)
        {
            var fixture = active;
            if (fixture == null || fixture.done || fixture.source == null) return;
            bool isUnsafe = GameloopManager.IsNativeContainerAccessUnsafe;
            if (fixture.lateUpdateUnsafe == isUnsafe) return;
            fixture.lateUpdateUnsafe = isUnsafe;
            fixture.Record("hud-late-update", $"unsafe={isUnsafe};jobsStarted={GameloopManager.jobStarted};active={__instance.gameObject.activeInHierarchy}");
        }

        private static void ObserveWidget(UI_WaypointWidget __instance, UI_MainWaypoints.WaypointData __0)
        {
            var fixture = active;
            var anchor = fixture?.source;
            if (fixture == null || anchor == null || fixture.disposed || fixture.done
                || __0.transformId != anchor.transformId || !__0.color.Equals(MarkerColor)
                || __0.position != fixture.localPoint || !__instance.gameObject.activeInHierarchy) return;
            try
            {
                var renderedWorld = __0.WorldPosition;
                Require(float.IsFinite(renderedWorld.sqrMagnitude)
                    && Vector3.Distance(renderedWorld, anchor.TransformData.LocalToWorld(fixture.localPoint)) < 0.025f,
                    "Native HUD marker resolved a different anchor position.");
                var renderedPoint = PositionManager.ToSystemPosition(renderedWorld);
                if (!fixture.displayed)
                {
                    fixture.widget = __instance;
                    fixture.firstPoint = renderedPoint;
                    fixture.displayed = true;
                    fixture.Record("native-widget-visible", $"transform={anchor.transformId}");
                }
                else
                {
                    Require(ReferenceEquals(fixture.widget, __instance), "The owned widget was unexpectedly replaced.");
                    var travel = Vector3l.Distance(fixture.firstPoint, renderedPoint);
                    if (!fixture.moved && travel > 2f)
                    {
                        fixture.moved = true;
                        fixture.Record("native-widget-moved", FormattableString.Invariant($"travel={travel:F2}m"));
                    }
                }
            }
            catch (Exception exception) { fixture.Fail(exception); }
        }

        private static bool Tracked(ParentEntity grid)
        {
            int id = grid.TrackingDataId;
            if (!HUD_TrackingWidgetResources.datas.IsCreated || id <= 0 || id >= HUD_TrackingWidgetResources.datas.Length)
                return false;
            var row = HUD_TrackingWidgetResources.datas[id];
            return row.IsValid && !row.IsHidden && row.IsEntity && row.transformId == grid.transformId
                && ReferenceEquals(row.GetEntity(), grid);
        }

        private static bool Owned(ParentEntity grid, string expected)
            => grid.EntityName == expected && grid.IsInitialized && !grid.saveInWorld && !grid.isDestroyed && !grid.WillBeUnspawned
                && ReferenceEquals(grid.ID.Entity, grid) && grid.Sync != null
                && grid.Sync.instanceData.ownerPlayerNetID == Dependency.PlayerManager.NetworkSetup.netId;

        private bool Ready()
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && !PlayerState.InMenu
                && NetworkServer.active && NetworkServer.dontListen && Dependency.PlayerManager != null
                && Dependency.PlayerManager.IsLocal && SaveManager.CharacterName == "ConnectorProbe-" + session.Substring(0, 8);

        private void OnDestroy()
        {
            if (ReferenceEquals(active, this)) active = null;
            marker?.Dispose();
            observer?.UnpatchSelf();
        }

        private void Fail(Exception exception)
        {
            marker?.Dispose();
            marker = null;
            done = true;
            Record("fail", exception.Message);
        }

        private void Record(string stage, string detail)
            => Logger.LogInfo($"KEEL_NAVIGATION|{session}|{stage}|{detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')}");

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
