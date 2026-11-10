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
        public static bool IsPointMotionAvailable { get; internal set; }

        internal static void Bind()
        {
            hasLocalAuthority = AccessTools.StructFieldRefAccess<SkyRigidbody.Data, bool>("hasLocalAuthority");
            isKinematic = AccessTools.StructFieldRefAccess<SkyRigidbody.Data, bool>("isKinematic");
        }

        /// <summary>Reads a grid point's system position and native world-frame velocity during a physics callback.</summary>
        public static bool TryReadPointMotion(in GridPhysicsContext context, ParentEntity grid, Vector3 gridLocalPoint,
            out Vector3l systemPosition, out Vector3 worldVelocity)
        {
            systemPosition = default;
            worldVelocity = default;
            return IsPointMotionAvailable && GridPhysicsStep.CanRead(context) && IsValidLocalPoint(gridLocalPoint)
                && TryReadPointMotionNative(grid, gridLocalPoint, out systemPosition, out worldVelocity);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryReadPointMotionNative(ParentEntity grid, Vector3 local, out Vector3l position, out Vector3 velocity)
        {
            position = default;
            velocity = default;
            if (!TryGetDynamicBody(grid, out var handle)) return false;
            var point = grid.TransformData.LocalToSystem(local);
            var motion = handle.Data.VelocityAtSystemPoint(point);
            if (!float.IsFinite(motion.x) || !float.IsFinite(motion.y) || !float.IsFinite(motion.z)) return false;
            position = point;
            velocity = motion;
            return true;
        }

        /// <summary>Applies drag at a grid-local point using world-frame velocities supplied by the caller.</summary>
        public static bool TryApplyAtmosphericDrag(in GridPhysicsContext context, ParentEntity grid,
            Vector3 gridLocalPoint, float relativeDensity, Vector3 pointWorldVelocity, Vector3 airWorldVelocity,
            float coefficient)
        {
            if (!GridPhysicsStep.CanApply(context)
                || !AtmosphericDrag.TryCalculate(relativeDensity,
                    new System.Numerics.Vector3(pointWorldVelocity.x, pointWorldVelocity.y, pointWorldVelocity.z),
                    new System.Numerics.Vector3(airWorldVelocity.x, airWorldVelocity.y, airWorldVelocity.z),
                    coefficient, out var force))
                return false;
            return TryApplyAtGridPoint(context, grid, gridLocalPoint, new Vector3(force.X, force.Y, force.Z));
        }

        /// <summary>Revalidates a saved contact and applies a compression force within the declared reach.</summary>
        /// <remarks>Supply the application point's world-frame velocity relative to terrain.</remarks>
        public static bool TryApplyTerrainSuspension(in GridPhysicsContext context, ParentEntity grid,
            in TerrainAnchorPose anchor, Vector3 gridLocalPoint, Vector3 pointRelativeVelocity,
            float restLength, float stiffness, float damping)
        {
            if (!GridPhysicsStep.CanApply(context) || !IsValidLocalPoint(gridLocalPoint)
                || !TryGetDynamicBody(grid, out _)
                || !float.IsFinite(restLength) || restLength <= 0f
                || restLength > MaximumLocalPointDistance
                || TerrainAnchor.Resolve(anchor, out var surface, out var normal, grid) != TerrainAnchorResult.Resolved)
                return false;
            var pointWorldPosition = PositionManager.ToWorldPosition(grid.TransformData.LocalToSystem(gridLocalPoint));
            var offset = pointWorldPosition - surface;
            if (!float.IsFinite(offset.sqrMagnitude) || offset.sqrMagnitude >= restLength * restLength
                || !TerrainSuspension.TryCalculate(Vector3.Dot(offset, normal), Vector3.Dot(pointRelativeVelocity, normal),
                    restLength, stiffness, damping, out float magnitude))
                return false;
            return TryApplyAtGridPoint(context, grid, gridLocalPoint, normal * magnitude);
        }

        /// <summary>Queues a world-space force at the given grid-local position, in metres.</summary>
        public static bool TryApplyAtGridPoint(in GridPhysicsContext context, ParentEntity grid,
            Vector3 gridLocalPoint, Vector3 worldForce)
        {
            float forceSquared = worldForce.sqrMagnitude;
            if (!GridPhysicsStep.CanApply(context) || !float.IsFinite(forceSquared)
                || forceSquared <= 0f || forceSquared > MaximumForce * MaximumForce
                || !IsValidLocalPoint(gridLocalPoint) || !TryGetDynamicBody(grid, out var handle))
                return false;

            handle.AddForceAtSystemPosition(worldForce, grid.TransformData.LocalToSystem(gridLocalPoint));
            GridPhysicsStep.ConsumeForce();
            return true;
        }

        private static bool IsValidLocalPoint(Vector3 point)
        {
            float squared = point.sqrMagnitude;
            return float.IsFinite(squared) && squared <= MaximumLocalPointDistance * MaximumLocalPointDistance;
        }

        private static bool TryGetDynamicBody(ParentEntity grid, out SkyRigidbody.Handle handle)
        {
            handle = default;
            if (grid == null || grid.Rigidbody is not { } body || body.isParented) return false;
            handle = body.OwnSkyRigidbody;
            if (!handle.IsValid || handle.id <= 0 || !SkyRigidbody.datas.IsCreated || handle.id >= SkyRigidbody.datas.Length
                || handle.id >= SkyRigidbody.Entities.Count || !ReferenceEquals(SkyRigidbody.Entities[handle.id], grid))
                return false;
            var data = handle.Data;
            if (data.isForcedStatic || data.forceSleeping || data.transformId != grid.transformId
                || !float.IsFinite(data.mass) || data.mass <= 0f
                || hasLocalAuthority == null || !hasLocalAuthority(ref data) || isKinematic == null || isKinematic(ref data))
                return false;

            return true;
        }
    }
}
