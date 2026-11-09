using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx.Configuration;
using Game;
using HarmonyLib;
using Manager;
using Mirror;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Temporarily follows a loaded grid with the local stock view.</summary>
    public static class CameraViewService
    {
        private const float MaximumRange = 250f;
        private static readonly KeyboardShortcut ReleaseShortcut = new KeyboardShortcut(KeyCode.Escape);
        private static FieldInfo? photoField;
        private static FieldInfo? pendingEntries;
        private static View? active;
        private static bool enabled;

        public static bool TryFollow(ParentEntity grid, Vector3 localOffset, Quaternion localRotation, out IDisposable? lease)
        {
            lease = null;
            var player = Dependency.PlayerManager;
            float length = Quaternion.Dot(localRotation, localRotation);
            if (!enabled || active != null || !IsNormalView(player) || grid == null || !IsLive(grid)
                || !float.IsFinite(localOffset.sqrMagnitude) || localOffset.sqrMagnitude > MaximumRange * MaximumRange
                || !float.IsFinite(length) || Mathf.Abs(length - 1f) > 0.001f)
                return false;

            if (photoField?.GetValue(player.PlayerActions) is not PlayerPhotoMode photo) return false;
            var anchor = player.CameraTransform.parent;
            if (anchor == null || anchor.parent != player.transform) return false;
            var view = new View(player, grid, anchor, photo, localOffset, localRotation);
            if (!view.TryFollow())
            {
                view.DestroyProxy();
                return false;
            }

            active = view;
            lease = view;
            return true;
        }

        internal static void Install(Harmony harmony)
        {
            photoField = AccessTools.DeclaredField(typeof(PlayerActions), "playerPhotoMode");
            if (photoField?.FieldType != typeof(PlayerPhotoMode)) throw new InvalidOperationException("Native photo gate field changed.");
            pendingEntries = AccessTools.DeclaredField(typeof(Network.NetworkedCommandActivator), "commandToEnter");
            if (pendingEntries?.FieldType != typeof(List<BrickCoord>))
                throw new InvalidOperationException("Native command entry queue changed.");
            var camera = AccessTools.DeclaredMethod(typeof(PlayerCameraControl), nameof(PlayerCameraControl.OnUpdate), new[] { typeof(float) });
            var pose = AccessTools.DeclaredMethod(typeof(PlayerCameraControl), "SetCameraTransform", new[] { typeof(float) });
            var update = AccessTools.DeclaredMethod(typeof(PlayerActions), nameof(PlayerActions.UpdateCommand), Type.EmptyTypes);
            var menus = AccessTools.DeclaredMethod(typeof(PlayerMenuActions), nameof(PlayerMenuActions.OnUpdate), Type.EmptyTypes);
            var cameraPrefix = Patch(nameof(BeforeCameraUpdate));
            var cameraPostfix = Patch(nameof(AfterCameraUpdate));
            var actions = Patch(nameof(RouteActionGate));
            var menuPrefix = Patch(nameof(BeforeMenuUpdate));
            try
            {
                harmony.Patch(camera, prefix: new HarmonyMethod(cameraPrefix));
                harmony.Patch(pose, postfix: new HarmonyMethod(cameraPostfix));
                harmony.Patch(update, transpiler: new HarmonyMethod(actions));
                harmony.Patch(menus, prefix: new HarmonyMethod(menuPrefix));
                enabled = true;
            }
            catch
            {
                harmony.Unpatch(camera, cameraPrefix);
                harmony.Unpatch(pose, cameraPostfix);
                harmony.Unpatch(update, actions);
                harmony.Unpatch(menus, menuPrefix);
                throw;
            }
        }

        internal static void Disable()
        {
            enabled = false;
            ResetWorld();
        }

        internal static void ResetWorld()
        {
            var view = active;
            if (view == null) return;
            view.Dispose();
            // Detach before native scene teardown can destroy either hierarchy.
            if (view.Player != null && view.Anchor != null && view.OwnsAnchor)
                view.Camera.ParentCameraOn(null, Vector3.zero, Quaternion.identity);
            if (!view.OwnsAnchor) view.DestroyProxy();
            if (view.Player == null || !ReferenceEquals(Dependency.PlayerManager, view.Player)) active = null;
        }

        private static MethodInfo Patch(string name)
            => AccessTools.DeclaredMethod(typeof(CameraViewService), name);

        private static bool BeforeCameraUpdate(PlayerCameraControl __instance)
        {
            var view = active;
            if (view == null) return true;
            if (!ReferenceEquals(view.Camera, __instance))
            {
                if (view.Player == null || !ReferenceEquals(Dependency.PlayerManager, view.Player)) ResetWorld();
                return true;
            }
            if (!view.Releasing && enabled && IsNormalView(view.Player) && IsLive(view.Grid) && view.TryFollow())
                return false;
            view.Dispose();
            return true;
        }

        private static void AfterCameraUpdate(PlayerCameraControl __instance)
        {
            var view = active;
            if (view == null || !view.Releasing || !ReferenceEquals(view.Camera, __instance) || !IsNormalView(view.Player))
                return;
            var anchor = view.Anchor;
            if (anchor == null || anchor.parent != view.Player.transform || anchor.localPosition != Vector3.zero
                || Quaternion.Angle(anchor.localRotation, Quaternion.identity) > 0.001f || anchor.localScale != Vector3.one)
                return;
            view.DestroyProxy();
            active = null;
        }

        private static bool BeforeMenuUpdate()
        {
            var view = active;
            if (view == null) return true;
            if (ReleaseShortcut.IsDown()) view.Dispose();
            return false;
        }

        private static bool ShouldReleaseActionInputs(PlayerPhotoMode photo)
            => active is { } view && ReferenceEquals(view.Photo, photo) || photo.UpdatePhotoMode();

        private static IEnumerable<CodeInstruction> RouteActionGate(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var stock = AccessTools.DeclaredMethod(typeof(PlayerPhotoMode), nameof(PlayerPhotoMode.UpdatePhotoMode), Type.EmptyTypes);
            int index = code.FindIndex(instruction => instruction.Calls(stock));
            if (index < 0 || code.Count(instruction => instruction.Calls(stock)) != 1)
                throw new InvalidOperationException("The native camera action gate changed during patching.");
            var call = code[index];
            call.opcode = OpCodes.Call;
            call.operand = Patch(nameof(ShouldReleaseActionInputs));
            return code;
        }

        private static bool IsNormalView(PlayerCommandManager player)
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                && player != null && ReferenceEquals(Dependency.PlayerManager, player) && player.IsLocal
                && player.PlayerCameraControl != null && player.PlayerActions != null && player.CameraTransform != null
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && NetworkServer.active && NetworkServer.dontListen
                && !PlayerState.InMenu && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
                && !PlayerState.SystemEntered && !PlayerState.IsControllingVehicle && !PlayerState.ViewLocked
                && player.CommandActivator != null && !player.CommandActivator.IsInEnterCommand
                && pendingEntries?.GetValue(player.CommandActivator) is List<BrickCoord> { Count: 0 }
                && !player.IsSeated && !player.Health.IsRespawning && !player.lookData.isInCamera && !player.lookData.isInTurret
                && !player.lookData.isInMedbay && !PlayerPhotoMode.IsInPhotoMode && Time.timeScale > 0.5f;

        private static bool IsLive(ParentEntity grid)
            => grid != null && grid.IsInitialized && !grid.isDestroyed && !grid.WillBeUnspawned
                && ReferenceEquals(grid.ID.Entity, grid);

        private sealed class View : IDisposable
        {
            internal readonly PlayerCommandManager Player;
            internal readonly PlayerCameraControl Camera;
            internal readonly PlayerPhotoMode Photo;
            internal readonly ParentEntity Grid;
            internal readonly Transform Anchor;
            private readonly Vector3 offset;
            private readonly Quaternion rotation;
            private GameObject? proxy;
            private int releasing;

            internal View(PlayerCommandManager player, ParentEntity grid, Transform anchor, PlayerPhotoMode photo, Vector3 offset, Quaternion rotation)
            {
                Player = player;
                Camera = player.PlayerCameraControl;
                Photo = photo;
                Grid = grid;
                Anchor = anchor;
                this.offset = offset;
                this.rotation = rotation;
            }

            internal bool Releasing => Volatile.Read(ref releasing) != 0;
            internal bool OwnsAnchor => proxy != null && Anchor != null && Anchor.parent == proxy.transform;

            internal bool TryFollow()
            {
                if (!Grid.SkyTransform.CurrentSystem.Equals(Player.SkyTransform.CurrentSystem)) return false;
                var data = Grid.TransformData;
                Vector3 position = data.LocalToWorld(offset);
                if (!float.IsFinite(position.sqrMagnitude)
                    || (position - Player.transform.position).sqrMagnitude > MaximumRange * MaximumRange)
                    return false;
                if (proxy == null)
                {
                    proxy = new GameObject("Keel Camera View") { hideFlags = HideFlags.DontSave };
                    proxy.transform.SetParent(Player.transform, worldPositionStays: true);
                    proxy.transform.SetPositionAndRotation(position, data.rotation * rotation);
                    Camera.ParentCameraOn(proxy.transform, Vector3.zero, Quaternion.identity);
                }
                else
                {
                    if (!OwnsAnchor) return false;
                    proxy.transform.SetPositionAndRotation(position, data.rotation * rotation);
                }
                return true;
            }

            public void Dispose() => Interlocked.Exchange(ref releasing, 1);

            internal void DestroyProxy()
            {
                if (proxy != null && !OwnsAnchor) UnityEngine.Object.Destroy(proxy);
                proxy = null;
            }
        }
    }
}
