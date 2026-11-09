using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Applies native world-space force and torque to a live parent grid during its physics callback.</summary>
    public static class GridForceService
    {
        public const float MaximumForce = 1_000_000_000f;
        public const float MaximumLocalPointDistance = 4_096f;
        private static AccessTools.StructFieldRef<SkyRigidbody.Data, bool>? hasLocalAuthority;
        private static AccessTools.StructFieldRef<SkyRigidbody.Data, bool>? isKinematic;

        internal static void Bind()
        {
            hasLocalAuthority = AccessTools.StructFieldRefAccess<SkyRigidbody.Data, bool>("hasLocalAuthority");
            isKinematic = AccessTools.StructFieldRefAccess<SkyRigidbody.Data, bool>("isKinematic");
        }

        /// <summary>Queues a world-space force at the given grid-local position, in metres.</summary>
        public static bool TryApplyAtGridPoint(in GridPhysicsContext context, ParentEntity grid,
            Vector3 gridLocalPoint, Vector3 worldForce)
        {
            float forceSquared = worldForce.sqrMagnitude;
            float offsetSquared = gridLocalPoint.sqrMagnitude;
            if (!GridPhysicsStep.CanApply(context) || !float.IsFinite(forceSquared)
                || forceSquared <= 0f || forceSquared > MaximumForce * MaximumForce
                || !float.IsFinite(offsetSquared) || offsetSquared > MaximumLocalPointDistance * MaximumLocalPointDistance
                || grid == null || grid.Rigidbody is not { } body || body.isParented)
                return false;

            var handle = body.OwnSkyRigidbody;
            if (!handle.IsValid || handle.id <= 0 || !SkyRigidbody.datas.IsCreated || handle.id >= SkyRigidbody.datas.Length
                || handle.id >= SkyRigidbody.Entities.Count || !ReferenceEquals(SkyRigidbody.Entities[handle.id], grid))
                return false;
            var data = handle.Data;
            if (data.isForcedStatic || data.forceSleeping || data.transformId != grid.transformId
                || !float.IsFinite(data.mass) || data.mass <= 0f
                || hasLocalAuthority == null || !hasLocalAuthority(ref data) || isKinematic == null || isKinematic(ref data))
                return false;

            handle.AddForceAtSystemPosition(worldForce, grid.TransformData.LocalToSystem(gridLocalPoint));
            GridPhysicsStep.ConsumeForce();
            return true;
        }
    }
}
