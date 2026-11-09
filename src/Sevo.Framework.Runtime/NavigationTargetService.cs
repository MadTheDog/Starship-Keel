using Manager;
using UI;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Reads and selects existing visible entity targets for the locally controlled ship.</summary>
    public static class NavigationTargetService
    {
        private static bool enabled;

        public static bool TrySelectEntity(ParentEntity target)
        {
            if (!TryGetShip(out var ship) || target == null || ReferenceEquals(ship.entity, target)
                || !TryReadTrackedTarget(target, out _))
                return false;
            HUD_TargetManager.OnNewHolomapTarget(target.TrackingDataId, ship);
            return HUD_TargetManager.CurrentTargetId == target.TrackingDataId;
        }

        public static bool TryGetSelectedEntity(out ParentEntity? target)
        {
            target = null;
            return TryGetShip(out _) && TryReadTrackedEntity(HUD_TargetManager.CurrentTargetId, out target, out _);
        }

        internal static void Install() => enabled = true;

        internal static void Disable() => enabled = false;

        private static bool TryGetShip(out Spaceship ship)
        {
            ship = null!;
            if (!enabled || !NavigationWorld.CanRead || !HUD_TargetManager.IsActive.Value
                || ReferenceEquals(Dependency.PlayerNetwork, null)) return false;
            var controlled = HUD_TargetManager.Spaceship;
            if (controlled == null || controlled.entity == null || !controlled.IsControlledByLocalPlayer
                || !ReferenceEquals(controlled.entity, HUD_TargetManager.PlayerEntity)
                || !NavigationWorld.TryReadTransform(controlled.entity, out _)) return false;
            ship = controlled;
            return true;
        }

        internal static bool TryReadTrackedTarget(ParentEntity target, out TrackingData data)
        {
            data = default;
            return NavigationWorld.CanRead && target != null
                && TryReadTrackedEntity(target.TrackingDataId, out var current, out data) && ReferenceEquals(current, target);
        }

        private static bool TryReadTrackedEntity(int id, out ParentEntity? target, out TrackingData data)
        {
            target = null;
            data = default;
            if (!HUD_TrackingWidgetResources.datas.IsCreated || id <= 0 || id >= HUD_TrackingWidgetResources.datas.Length)
                return false;
            var row = HUD_TrackingWidgetResources.datas[id];
            if (!row.IsValid || row.IsHidden || !row.IsEntity) return false;
            var entity = row.GetEntity();
            if (entity == null || entity.TrackingDataId != id
                || !NavigationWorld.TryReadTransform(entity, out var transform) || row.transformId != transform)
                return false;
            target = entity;
            data = row;
            return true;
        }
    }
}
