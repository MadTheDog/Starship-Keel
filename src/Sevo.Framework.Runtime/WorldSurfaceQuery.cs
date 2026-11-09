using Manager;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public enum WorldSurfaceQueryResult { Unavailable, InvalidRay, Miss, Hit }
    public enum WorldSurfaceKind { Grid, Terrain, Water, Other }

    /// <summary>A value snapshot in the floating-origin world frame at the time of the query.</summary>
    public readonly struct WorldSurfaceHit
    {
        public WorldSurfaceKind Kind { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public float Distance { get; }
        public bool HasNormal { get; }

        internal WorldSurfaceHit(WorldSurfaceKind kind, Vector3 point, Vector3 normal, float distance, bool hasNormal)
        {
            Kind = kind;
            Point = point;
            Normal = normal;
            Distance = distance;
            HasNormal = hasNormal;
        }
    }

    /// <summary>Queries the nearest loaded surface through the game's native voxel physics.</summary>
    public static class WorldSurfaceQuery
    {
        public const float MaximumDistance = 2048f;
        private static VoxelRaycast? solid;
        private static VoxelRaycast? wet;

        public static bool IsAvailable => solid != null;

        internal static void Enable()
        {
            wet = new VoxelRaycast { includeWater = true };
            solid = new VoxelRaycast();
        }

        internal static void Disable()
        {
            solid = null;
            wet = null;
        }

        /// <summary>Call on the game thread during a native-container-safe phase of a loaded solo session.</summary>
        /// <remarks>Supply a currently live parent grid to ignore a sensor's own construct.</remarks>
        public static WorldSurfaceQueryResult Raycast(Vector3 origin, Vector3 direction, float distance,
            bool includeWater, out WorldSurfaceHit hit, ParentEntity? ignoreGrid = null)
        {
            hit = default;
            var result = RaycastNative(origin, direction, distance, includeWater, out var native, out var normalized, ignoreGrid);
            if (result != WorldSurfaceQueryResult.Hit) return result;

            var kind = native.type switch
            {
                VoxelRaycastHit.Type.voxel => WorldSurfaceKind.Grid,
                VoxelRaycastHit.Type.terrain or VoxelRaycastHit.Type.newTerrain => WorldSurfaceKind.Terrain,
                VoxelRaycastHit.Type.water => WorldSurfaceKind.Water,
                _ => WorldSurfaceKind.Other
            };
            // The ocean query updates only distance and type, leaving the previous collider's normal behind.
            bool hasNormal = kind is WorldSurfaceKind.Grid or WorldSurfaceKind.Terrain;
            var normal = hasNormal ? native.WorldNormal : Vector3.zero;
            hasNormal = hasNormal && IsFinite(normal) && normal.sqrMagnitude > 0.000001f;
            hit = new WorldSurfaceHit(kind, origin + normalized * native.distance,
                hasNormal ? normal : Vector3.zero, native.distance, hasNormal);
            return WorldSurfaceQueryResult.Hit;
        }

        internal static WorldSurfaceQueryResult RaycastNative(Vector3 origin, Vector3 direction, float distance,
            bool includeWater, out VoxelRaycastHit hit, out Vector3 normalized, ParentEntity? ignoreGrid)
        {
            hit = default;
            normalized = default;
            var query = includeWater ? wet : solid;
            if (query == null || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || !FunctionalCommandState.IsSolo || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle)
                return WorldSurfaceQueryResult.Unavailable;

            float magnitude = direction.magnitude;
            if (!IsFinite(origin) || !float.IsFinite(magnitude) || magnitude < 0.00001f
                || !float.IsFinite(distance) || distance <= 0f || distance > MaximumDistance)
                return WorldSurfaceQueryResult.InvalidRay;

            normalized = direction / magnitude;
            if (!query.Raycast(origin, normalized, out hit, distance, ignoreGrid == null ? null : ignoreGrid))
                return WorldSurfaceQueryResult.Miss;

            return float.IsFinite(hit.distance) && hit.distance >= 0f && hit.distance <= distance
                ? WorldSurfaceQueryResult.Hit : WorldSurfaceQueryResult.Unavailable;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
