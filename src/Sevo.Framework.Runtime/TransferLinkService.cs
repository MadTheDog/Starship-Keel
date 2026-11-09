using System;
using System.Collections.Generic;
using HarmonyLib;
using Manager;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A disposable link between two loaded native storage hose endpoints.</summary>
    public sealed class TemporaryTransferLink : IDisposable
    {
        private volatile bool releaseRequested;
        internal bool ReleaseRequested => releaseRequested;
        public bool IsActive { get; internal set; } = true;

        /// <summary>Schedules native graph unlinking at the next safe frame boundary.</summary>
        public void Dispose() => releaseRequested = true;
    }

    public static class TransferLinkService
    {
        private const int RemotePort = 1;
        private static readonly List<Link> Links = new List<Link>();
        private static readonly HashSet<int> Reserved = new HashSet<int>();
        private static bool enabled;

        /// <summary>Connects two free, same-medium stock hose ends in a loaded solo world.</summary>
        public static bool TryConnect(BrickCoord first, BrickCoord second, float maximumDistance,
            out TemporaryTransferLink? connection)
        {
            connection = null;
            if (!CanAccess || !(maximumDistance > 0f) || maximumDistance > 64f
                || !TryResolve(first, out var a) || !TryResolve(second, out var b)
                || ReferenceEquals(a.Parent, b.Parent) || a.Type != b.Type
                || first.brickData.gridLevel != second.brickData.gridLevel
                || Reserved.Contains(a.Id) || Reserved.Contains(b.Id)
                || Neighbor(a.Id) != 0 || Neighbor(b.Id) != 0)
                return false;
            var link = new Link(a, b, maximumDistance);
            if (!link.InRange || !StorageConnectionLogic.LinkPipe(a.Type, a.Id, b.Id, RemotePort))
                return false;
            if (!StorageConnectionLogic.LinkPipe(b.Type, b.Id, a.Id, RemotePort))
            {
                StorageConnectionLogic.Unlink(a.Id, b.Id);
                return false;
            }
            Reserved.Add(a.Id);
            Reserved.Add(b.Id);
            Links.Add(link);
            connection = link.Handle;
            return true;
        }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CommandDataHose), nameof(CommandDataHose.CanLinkToCommand)),
                prefix: new HarmonyMethod(typeof(TransferLinkService), nameof(KeepReserved)));
            harmony.Patch(AccessTools.Method(typeof(CommandDataHose), nameof(CommandDataHose.CanBeLinkTargetFromCommand)),
                prefix: new HarmonyMethod(typeof(TransferLinkService), nameof(KeepReserved)));
            harmony.Patch(AccessTools.Method(typeof(CommandDataHose), nameof(CommandDataHose.OnDelete)),
                prefix: new HarmonyMethod(typeof(TransferLinkService), nameof(BeforeHoseDelete)));
            harmony.Patch(AccessTools.Method(typeof(GameloopManager), nameof(GameloopManager.OnAfterRendering)),
                postfix: new HarmonyMethod(typeof(TransferLinkService), nameof(ReleaseExpired)));
            enabled = true;
        }

        internal static void Disable()
        {
            if (CanAccess)
                for (int i = Links.Count - 1; i >= 0; i--) Release(i);
            foreach (var link in Links) link.Handle.IsActive = false;
            Links.Clear();
            Reserved.Clear();
            enabled = false;
        }

        private static bool CanAccess => enabled && GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle;

        private static bool KeepReserved(ref CommandDataHose __instance, ref bool __result)
        {
            if (!Reserved.Contains(__instance.StorageId)) return true;
            __result = false;
            return false;
        }

        private static void BeforeHoseDelete(ref CommandDataHose __instance)
        {
            for (int i = Links.Count - 1; i >= 0; i--)
            {
                var link = Links[i];
                if (link.A.Id != __instance.StorageId && link.B.Id != __instance.StorageId) continue;
                // Stock OnBrickRemoved clears both directions before releasing the storage ID.
                Forget(i);
            }
        }

        private static void ReleaseExpired()
        {
            if (!CanAccess) return;
            for (int i = Links.Count - 1; i >= 0; i--)
            {
                var link = Links[i];
                if (link.Handle.ReleaseRequested || !link.A.IsUsable || !link.B.IsUsable || !link.InRange)
                    Release(i);
            }
        }

        private static void Release(int index)
        {
            var link = Links[index];
            if (link.A.IsLive && link.B.IsLive)
            {
                if (Neighbor(link.A.Id) == link.B.Id) StorageConnectionLogic.Unlink(link.A.Id, link.B.Id);
                if (Neighbor(link.B.Id) == link.A.Id) StorageConnectionLogic.Unlink(link.B.Id, link.A.Id);
            }
            Forget(index);
        }

        private static void Forget(int index)
        {
            var link = Links[index];
            link.Handle.IsActive = false;
            Reserved.Remove(link.A.Id);
            Reserved.Remove(link.B.Id);
            Links.RemoveAt(index);
        }

        private static unsafe int Neighbor(int id)
        {
            var data = StorageResources.GetStorageData(id);
            return data.neighbors[RemotePort];
        }

        private static bool TryResolve(BrickCoord recorded, out Endpoint endpoint, bool requireUsable = true)
        {
            endpoint = default;
            var grid = recorded.entity;
            if (grid == null || grid.Parent is not { } parent
                || requireUsable && (!parent.Identity.IsPlayerOwned || parent.isDestroyed)
                || recorded.brickData.instanceId <= 0)
                return false;
            var current = grid.Bricks.GetBrickData(recorded.brickData.instanceId);
            if (!current.IsValid || requireUsable && current.healthScore == 0
                || current.brickId != recorded.brickData.brickId || current.gridPosition != recorded.brickData.gridPosition
                || current.gridLevel != recorded.brickData.gridLevel || current.rotation != recorded.brickData.rotation)
                return false;
            var live = new BrickCoord(grid.ID, current);
            if (live.Command is not CommandDataHose hose || hose.StorageId <= 0
                || hose.ItemStorageType is not (ItemStorageType.hose or ItemStorageType.fuelHose))
                return false;
            endpoint = new Endpoint(live, grid, parent, hose.StorageId, hose.ItemStorageType);
            return true;
        }

        private readonly struct Endpoint
        {
            internal readonly BrickCoord Placement;
            internal readonly VoxelEntity Grid;
            internal readonly ParentEntity Parent;
            internal readonly int Id;
            internal readonly ItemStorageType Type;

            internal Endpoint(BrickCoord placement, VoxelEntity grid, ParentEntity parent, int id, ItemStorageType type)
            {
                Placement = placement;
                Grid = grid;
                Parent = parent;
                Id = id;
                Type = type;
            }

            internal bool IsLive => Matches(requireUsable: false);
            internal bool IsUsable => Matches(requireUsable: true);

            private bool Matches(bool requireUsable) => Grid != null && ReferenceEquals(Placement.entity, Grid)
                && TryResolve(Placement, out var current, requireUsable)
                && current.Id == Id && ReferenceEquals(current.Parent, Parent);
        }

        private sealed class Link
        {
            internal readonly Endpoint A;
            internal readonly Endpoint B;
            internal readonly TemporaryTransferLink Handle = new TemporaryTransferLink();
            private readonly float maximumDistanceSquared;

            internal Link(Endpoint a, Endpoint b, float maximumDistance)
            {
                A = a;
                B = b;
                maximumDistanceSquared = maximumDistance * maximumDistance;
            }

            internal bool InRange => (A.Placement.WorldPosition - B.Placement.WorldPosition).sqrMagnitude <= maximumDistanceSquared;
        }
    }
}
