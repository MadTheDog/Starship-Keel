using System;
using System.Collections.Generic;
using Game;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public readonly struct SeatInteractionContext
    {
        public BrickCoord Seat { get; }

        internal SeatInteractionContext(BrickCoord seat) => Seat = seat;

        internal static bool TryGet(EnterCommandSeat command, PlayerCommandManager player, out SeatInteractionContext context)
        {
            context = default;
            if (!FunctionalCommandState.IsSolo || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle || command.IsRemoved
                || ReferenceEquals(player, null) || !ReferenceEquals(Dependency.PlayerManager, player)
                || !CommandCommon.IsLocalPlayer(player))
                return false;

            var placement = command.brickCoord;
            var entity = placement.entity;
            var parent = entity == null ? null : entity.Parent;
            if (entity == null || parent == null || !parent.IsInitialized || parent.WillBeUnspawned || parent.isDestroyed
                || entity.Bricks == null || placement.brickData.instanceId <= 0
                || placement.brickData.instanceId >= entity.Bricks.brickIds.Length)
                return false;
            var live = entity.Bricks.GetBrickData(placement.brickData.instanceId);
            if (!live.IsValid || live != placement.brickData || live.HealthFactor <= 0f
                || !ReferenceEquals(new BrickCoord(placement.entityID, live).Command, command))
                return false;

            context = new SeatInteractionContext(new BrickCoord(placement.entityID, live));
            return true;
        }

        internal static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    internal sealed class SeatProviderCatalog<T> where T : struct
    {
        private readonly SortedDictionary<string, Func<SeatInteractionContext, T?>> providers =
            new SortedDictionary<string, Func<SeatInteractionContext, T?>>(StringComparer.Ordinal);
        internal Action<string>? Report { get; set; }
        private bool sealedCatalog;

        internal void Register(string ownerId, string localName, Func<SeatInteractionContext, T?> provider)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (sealedCatalog) throw new InvalidOperationException("Register seat providers during plugin Awake.");
            providers.Add(key, provider);
        }

        internal bool TryResolve(SeatInteractionContext context, out T result)
        {
            sealedCatalog = true;
            foreach (var entry in providers)
            {
                try
                {
                    if (entry.Value(context) is T value)
                    {
                        result = value;
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    Report?.Invoke("Seat provider " + entry.Key + " failed: " + exception.Message);
                }
            }
            result = default;
            return false;
        }

        internal void Disable()
        {
            providers.Clear();
            Report = null;
            sealedCatalog = false;
        }
    }
}
