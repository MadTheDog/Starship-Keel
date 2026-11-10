using System;
using System.Runtime.CompilerServices;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    public enum TerrainAnchorResult { Unavailable, InvalidPose, BodyUnavailable, SurfaceMissing, Resolved }

    /// <summary>A persistent body-relative surface pose. The owning mod stores it with its anchor declaration.</summary>
    public readonly struct TerrainAnchorPose
    {
        public TerrainAnchorPose(BodyEnvironmentTarget body, Vector3 localPosition, Vector3 localNormal)
            => (Body, LocalPosition, LocalNormal) = (body, localPosition, localNormal);

        public BodyEnvironmentTarget Body { get; }
        public Vector3 LocalPosition { get; }
        public Vector3 LocalNormal { get; }
    }

    /// <summary>Captures and revalidates terrain contacts. No native docking or rigidbody state is retained.</summary>
    public static class TerrainAnchor
    {
        private const float CheckDistance = 0.5f;
        private static bool enabled;

        public static bool IsAvailable => enabled && WorldSurfaceQuery.IsAvailable;
        internal static void Enable() => enabled = true;
        internal static void Disable() => enabled = false;

        public static TerrainAnchorResult Capture(BodyEnvironmentTarget body, Vector3 origin, Vector3 direction,
            float distance, out TerrainAnchorPose pose, ParentEntity? ignoreGrid = null)
        {
            pose = default;
            if (!IsAvailable) return TerrainAnchorResult.Unavailable;
            return CaptureNative(body, origin, direction, distance, out pose, ignoreGrid);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TerrainAnchorResult CaptureNative(BodyEnvironmentTarget body, Vector3 origin, Vector3 direction,
            float distance, out TerrainAnchorPose pose, ParentEntity? ignoreGrid)
        {
            pose = default;
            if (body.StarSeed == 0) return TerrainAnchorResult.InvalidPose;
            var query = WorldSurfaceQuery.RaycastNative(origin, direction, distance, false, out var hit, out var axis, ignoreGrid);
            if (query == WorldSurfaceQueryResult.Unavailable) return TerrainAnchorResult.Unavailable;
            if (query == WorldSurfaceQueryResult.InvalidRay) return TerrainAnchorResult.InvalidPose;
            if (!TryBodyTransform(body, out int generationId, out var transform))
                return TerrainAnchorResult.BodyUnavailable;
            if (query != WorldSurfaceQueryResult.Hit || hit.type != VoxelRaycastHit.Type.newTerrain)
                return TerrainAnchorResult.SurfaceMissing;

            var nodes = OctreeNodeResources.OctreeNodeDatas;
            if (!nodes.IsCreated || hit.elementId <= 0 || hit.elementId >= nodes.Length)
                return TerrainAnchorResult.SurfaceMissing;
            var node = nodes[hit.elementId];
            if (node.transformId != hit.transformId || node.planetGenerationId != generationId || node.asteroidId != 0
                || !node.densityComputed || node.isJobStarted || node.isGeoDirty
                || node.geoState != NodeGeoState.hasGeo || node.fadeState == NodeFadeState.fadeOut
                || OctreeNodeJobs.toGeoCompute?.Contains(hit.elementId) == true)
                return TerrainAnchorResult.SurfaceMissing;

            var position = transform.SystemToLocal(PositionManager.ToSystemPosition(origin + axis * hit.distance));
            var normal = Quaternion.Inverse(transform.rotation) * hit.WorldNormal;
            if (!Finite(position) || !float.IsFinite(normal.sqrMagnitude) || normal.sqrMagnitude < 0.01f)
                return TerrainAnchorResult.SurfaceMissing;
            pose = new TerrainAnchorPose(body, position, normal.normalized);
            return TerrainAnchorResult.Resolved;
        }

        /// <summary>Resolves a saved pose against the current native collider. Stop applying attachment force unless it resolves.</summary>
        public static TerrainAnchorResult Resolve(in TerrainAnchorPose pose, out Vector3 point, out Vector3 normal,
            ParentEntity? ignoreGrid = null)
        {
            point = normal = default;
            return IsAvailable ? ResolveNative(pose, out point, out normal, ignoreGrid) : TerrainAnchorResult.Unavailable;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TerrainAnchorResult ResolveNative(in TerrainAnchorPose pose, out Vector3 point, out Vector3 normal,
            ParentEntity? ignoreGrid)
        {
            point = normal = default;
            if (!WorldSurfaceQuery.IsSafePhase) return TerrainAnchorResult.Unavailable;
            if (pose.Body.StarSeed == 0 || !Finite(pose.LocalPosition) || !Finite(pose.LocalNormal)
                || Math.Abs(pose.LocalNormal.sqrMagnitude - 1f) > 0.01f)
                return TerrainAnchorResult.InvalidPose;
            if (!TryBodyTransform(pose.Body, out _, out var transform)) return TerrainAnchorResult.BodyUnavailable;
            var candidate = PositionManager.ToWorldPosition(transform.LocalToSystem(pose.LocalPosition));
            var axis = transform.rotation * pose.LocalNormal;
            var result = Capture(pose.Body, candidate + axis * CheckDistance, -axis, CheckDistance * 2f,
                out var current, ignoreGrid);
            if (result != TerrainAnchorResult.Resolved) return result;
            if ((current.LocalPosition - pose.LocalPosition).sqrMagnitude > 0.01f
                || Vector3.Dot(pose.LocalNormal, current.LocalNormal) < 0.9f)
                return TerrainAnchorResult.SurfaceMissing;
            point = PositionManager.ToWorldPosition(transform.LocalToSystem(current.LocalPosition));
            normal = transform.rotation * current.LocalNormal;
            return TerrainAnchorResult.Resolved;
        }

        private static bool TryBodyTransform(BodyEnvironmentTarget body, out int generation,
            out SkyTransformData transform)
        {
            generation = 0;
            transform = default;
            var selected = LoadedRockyBody.Find(body.StarSeed, body.BodySeed);
            if (selected == null || selected.planetGenerationId == 0 || selected.transformId <= 0
                || !SkyTransformData.datas.IsCreated || selected.transformId >= SkyTransformData.datas.Length)
                return false;
            generation = selected.planetGenerationId;
            transform = SkyTransformData.datas[selected.transformId];
            return transform.scale == 1f;
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
