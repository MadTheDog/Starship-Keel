using System;
using System.Threading;
using Game;
using Manager;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Scoped access to compatible native tanks after stock jobs have finished.</summary>
    public sealed class FluidTickContext
    {
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private bool open = true;
        internal FluidTickContext(float deltaTime) => DeltaTime = deltaTime;
        public float DeltaTime { get; }
        internal void Close() => open = false;

        /// <summary>Configures a live empty tank selected by the mod. Existing contents and requests are preserved.</summary>
        public bool TryBindTank(BrickCoord placement, string fluidKey)
        {
            if (!TryResolveTank(placement, fluidKey, out var fluid, out var tank, out var storage, out _))
                return false;
            if (IsBound(storage, fluid))
                return true;
            if (storage.rootItemId != 0 || storage.currentVolume != 0f || storage.requestId != 0)
                return false;

            var settings = tank.GetSaveData();
            settings.fuelType = (int)fluid;
            settings.tankType = (int)FuelCommon.TankType.none;
            settings.shareOxygenToLifeSupport = false;
            tank.OnGenericParamsUpdated(settings, syncUpdate: false, onClose: false);
            return IsBound(StorageResources.GetStorageData(tank.StorageId), fluid);
        }

        public bool TryGetAmount(BrickCoord placement, string fluidKey, out float litres)
        {
            litres = 0f;
            if (!TryResolveTank(placement, fluidKey, out var fluid, out var tank, out var storage, out var context)
                || !IsBound(storage, fluid))
                return false;
            litres = FuelCommon.GetFuel(tank.StorageId, fluid, context);
            return float.IsFinite(litres) && litres >= 0f;
        }

        /// <summary>Produces up to the requested volume in the bound tank and returns litres inserted.</summary>
        public float Produce(BrickCoord placement, string fluidKey, float litres)
        {
            if (!float.IsFinite(litres) || litres <= 0f
                || !TryResolveTank(placement, fluidKey, out var fluid, out var tank, out var storage, out var context)
                || !IsBound(storage, fluid))
                return 0f;
            float before = FuelCommon.GetFuel(tank.StorageId, fluid, context);
            FuelCommon.FillTank(tank.StorageId, litres, fluid, context);
            return FuelCommon.GetFuel(tank.StorageId, fluid, context) - before;
        }

        /// <summary>Consumes up to the requested volume from the bound tank and returns litres removed.</summary>
        public float Consume(BrickCoord placement, string fluidKey, float litres)
        {
            if (!float.IsFinite(litres) || litres <= 0f
                || !TryResolveTank(placement, fluidKey, out var fluid, out var tank, out var storage, out var context)
                || !IsBound(storage, fluid))
                return 0f;
            float before = FuelCommon.GetFuel(tank.StorageId, fluid, context);
            FuelCommon.ConsumeFuelSafe(tank.StorageId, litres, fluid, context);
            return before - FuelCommon.GetFuel(tank.StorageId, fluid, context);
        }

        /// <summary>Queues a native pull for the bound tank's free capacity through its connected pipes.</summary>
        public bool TryRequestRefill(BrickCoord placement, string fluidKey)
        {
            if (!TryResolveTank(placement, fluidKey, out var fluid, out var tank, out var storage, out var context)
                || !IsBound(storage, fluid))
                return false;
            if (storage.requestId != 0)
            {
                var existing = context.GetRequestData(storage.requestId);
                if (existing.itemType != WorldItemType.fuel || existing.FuelType != fluid || existing.nextElementId != 0
                    || existing.requestType != RequestType.pull)
                    return false;
            }
            FuelCommon.RequestTankRefill(tank.StorageId, fluid, context);
            return true;
        }

        private bool TryResolveTank(BrickCoord placement, string key, out FuelCommon.FuelType fluid,
            out StorageCommandData tank, out StorageContainerData storage, out StorageContext context)
        {
            fluid = default;
            tank = default;
            storage = default;
            context = default;
            if (!open || Thread.CurrentThread.ManagedThreadId != thread || GameloopManager.IsNativeContainerAccessUnsafe
                || !FunctionalCommandState.IsSolo || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle
                || !FluidRegistration.TryResolve(key, out fluid) || !placement.IsValid || placement.brickData.instanceId < 0
                || !(placement.Command is StorageCommandData candidate) || candidate.StorageId <= 0)
                return false;

            context = StorageCommon.CONTEXT;
            if (candidate.StorageId >= context.StorageLength)
                return false;
            storage = context.GetStorageData(candidate.StorageId);
            if (storage.id != candidate.StorageId || storage.type != ItemStorageType.tank
                || storage.brickInstanceId != placement.brickData.instanceId
                || storage.childrenEntityId != placement.entity.ChildrenID
                || storage.parentEntityId != placement.entity.Parent.IndexID
                || !float.IsFinite(storage.maximumVolume) || storage.maximumVolume <= 0f
                || !float.IsFinite(storage.currentVolume) || storage.currentVolume < 0f)
                return false;
            tank = candidate;
            return true;
        }

        private static bool IsBound(StorageContainerData storage, FuelCommon.FuelType fluid) =>
            storage.fuelType == fluid && storage.tankType == FuelCommon.TankType.none && storage.transferMode == TransferMode.store;
    }
}
