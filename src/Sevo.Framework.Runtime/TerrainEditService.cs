using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using VoxelEngine;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    public enum TerrainEditOperation { CutAbove, FillBelow }
    public enum TerrainEditResult
    {
        Unavailable, InvalidBrush, NoTerrainHit, TerrainNotReady, FrameBudgetUsed, NoChange, Queued, Failed
    }

    /// <summary>Bounded planar edits of resident native terrain in a managed solo world.</summary>
    public static class TerrainEditService
    {
        public const float MaximumRadius = 4f;
        private const int MaximumNodes = 8;
        private static Action<string>? warn;
        private static bool enabled;
        private static int lastEditFrame = -1;

        public static bool IsAvailable => enabled && WorldSurfaceQuery.IsAvailable;

        internal static void Enable(Action<string> logWarning)
        {
            warn = logWarning;
            enabled = true;
            lastEditFrame = -1;
        }

        internal static void Disable()
        {
            enabled = false;
            warn = null;
        }

        /// <summary>Uses a fresh stock ray hit as the brush centre. The plane offset is in metres along its normal.</summary>
        /// <remarks>Queued means the density was changed and the native geometry and save work were scheduled.
        /// Call from the main thread in a native-container-safe phase. Positive offset moves the plane along its normal.
        /// Only resident mixed-geometry nodes can be edited by this preview.</remarks>
        public static TerrainEditResult ApplyPlane(Vector3 rayOrigin, Vector3 rayDirection, float rayDistance,
            Vector3 planeNormal, float planeOffset, float radius, TerrainEditOperation operation,
            ParentEntity? ignoreGrid = null)
        {
            if (!IsAvailable) return TerrainEditResult.Unavailable;
            float normalLength = planeNormal.magnitude;
            if (!float.IsFinite(normalLength) || normalLength < 0.00001f || !float.IsFinite(radius)
                || radius <= 0f || radius > MaximumRadius || !float.IsFinite(planeOffset)
                || Mathf.Abs(planeOffset) >= radius
                || operation is not (TerrainEditOperation.CutAbove or TerrainEditOperation.FillBelow))
                return TerrainEditResult.InvalidBrush;

            var query = WorldSurfaceQuery.RaycastNative(rayOrigin, rayDirection, rayDistance, false,
                out var hit, out _, ignoreGrid);
            if (query == WorldSurfaceQueryResult.Unavailable) return TerrainEditResult.Unavailable;
            if (query == WorldSurfaceQueryResult.InvalidRay) return TerrainEditResult.InvalidBrush;
            if (query != WorldSurfaceQueryResult.Hit || hit.type != VoxelRaycastHit.Type.newTerrain)
                return TerrainEditResult.NoTerrainHit;

            if (lastEditFrame == Time.frameCount) return TerrainEditResult.FrameBudgetUsed;
            lastEditFrame = Time.frameCount;

            try
            {
                return ApplyResidentPlane(hit, planeNormal / normalLength, planeOffset, radius,
                    operation == TerrainEditOperation.FillBelow);
            }
            catch (Exception exception)
            {
                enabled = false;
                warn?.Invoke("Terrain edits disabled after a native operation failed: " + exception.Message);
                return TerrainEditResult.Failed;
            }
        }

        private static TerrainEditResult ApplyResidentPlane(VoxelRaycastHit hit, Vector3 worldNormal,
            float planeOffset, float radius, bool fill)
        {
            var table = OctreeNodeResources.OctreeNodeDatas;
            if (!OctreeNodeResources.saveParsed || !table.IsCreated || hit.elementId <= 0 || hit.elementId >= table.Length)
                return TerrainEditResult.TerrainNotReady;

            var selected = table[hit.elementId];
            if (selected.transformId != hit.transformId || selected.planetGenerationId == 0 || selected.asteroidId != 0
                || !IsResident(selected) || selected.ultimateParentId <= 0 || selected.ultimateParentId >= table.Length
                || hit.TransformData.scale != 1f)
                return TerrainEditResult.TerrainNotReady;

            var root = table[selected.ultimateParentId];
            Vector3 centre = (Vector3)selected.position + hit.localPoint;
            Vector3 normal = Quaternion.Inverse(hit.TransformData.rotation) * worldNormal;
            var radiusVector = radius * Vector3.one;
            if (!root.DensityBoundsTight.Contains(centre - radiusVector)
                || !root.DensityBoundsTight.Contains(centre + radiusVector))
                return TerrainEditResult.TerrainNotReady;

            using var ids = new NativeList<int>(Allocator.Temp);
            root.GetOverlappingNodes(centre, ids, selected.ultimateParentId, radius);
            var nodes = new List<PreparedNode>(MaximumNodes);
            var densityIds = new HashSet<int>();
            try
            {
                VoxelNodeData fillMaterial = fill
                    ? ReadFillMaterial(selected, hit.localPoint, hit.localNormal)
                    : default;
                if (fill && (fillMaterial.Density >= 0f || !IsMaterialAvailable(fillMaterial)))
                    return TerrainEditResult.TerrainNotReady;

                foreach (int id in ids)
                {
                    if (id <= 0 || id >= table.Length) return TerrainEditResult.TerrainNotReady;
                    var node = table[id];
                    if (!OctreeNodeResources.ShouldSaveNode((int)node.VoxelSize)) continue;
                    if (!DensityLatticeTouches(node, centre, radiusVector)) continue;
                    if (nodes.Count == MaximumNodes || !IsResident(node) || (id != selected.ultimateParentId && node.ultimateParentId != selected.ultimateParentId)
                        || node.planetGenerationId != selected.planetGenerationId || node.asteroidId != 0 || !densityIds.Add(node.densitiesId))
                        return TerrainEditResult.TerrainNotReady;
                    var prepared = new PreparedNode(id, node);
                    nodes.Add(prepared);
                    prepared.Stage(centre, normal, planeOffset, radius, fill, fillMaterial);
                }

                if (!densityIds.Contains(selected.densitiesId))
                    return TerrainEditResult.TerrainNotReady;

                var changed = nodes.FindAll(node => node.Changed);
                if (changed.Count == 0) return TerrainEditResult.NoChange;

                // The stock queues consume the live density only later on the game thread.
                TerrainEditBatch.Commit(changed, node => node.Queue(), node => node.Write(),
                    node => node.Restore(), node => node.Description);
                foreach (var prepared in changed)
                {
                    try { prepared.RefreshProps(hit.SystemPosition); }
                    catch (Exception exception)
                    {
                        enabled = false;
                        warn?.Invoke("Terrain density committed and scheduled. Terrain edits disabled after stock prop refresh failed at "
                            + prepared.Description + ": " + exception.Message);
                        break;
                    }
                }
                return TerrainEditResult.Queued;
            }
            finally
            {
                foreach (var node in nodes) node.Dispose();
            }
        }

        private static bool DensityLatticeTouches(OctreeNodeData node, Vector3 centre, Vector3 radius)
        {
            int end = OctreeNodeData.DENSITY_WIDTH - 1;
            Vector3 first = (Vector3)node.position + (Vector3)node.GetPosition(0, 0, 0);
            Vector3 last = (Vector3)node.position + (Vector3)node.GetPosition(end, end, end);
            Vector3 min = centre - radius;
            Vector3 max = centre + radius;
            return first.x <= max.x && last.x >= min.x && first.y <= max.y && last.y >= min.y
                && first.z <= max.z && last.z >= min.z;
        }

        private static bool IsResident(OctreeNodeData node)
            => node.densityComputed && !node.isJobStarted && node.geoState == NodeGeoState.hasGeo
                && node.densitiesId > 0 && node.densitiesId < OctreeNodeResources.voxelNodeDatas.Length / OctreeNodeData.DENSITY_COUNT
                && float.IsFinite(node.VoxelSize) && node.VoxelSize > 0f && node.treeGuid != 0;

        private static VoxelNodeData ReadFillMaterial(OctreeNodeData node, Vector3 hit, Vector3 normal)
        {
            Vector3 sample = hit / node.VoxelSize - normal * 0.5f;
            int GridIndex(float value) => Mathf.Clamp(Mathf.FloorToInt(value) + OctreeNodeData.DENSITY_HW,
                0, OctreeNodeData.DENSITY_WIDTH - 1);
            int index = VoxelNodeData.ToDensityId(GridIndex(sample.x), GridIndex(sample.y), GridIndex(sample.z));
            return OctreeNodeResources.voxelNodeDatas[node.densitiesId * OctreeNodeData.DENSITY_COUNT + index];
        }

        private static bool IsMaterialAvailable(VoxelNodeData material)
            => material.texId0 < World.WorldTerrainResources.TerrainMaterialDatas.Length
                && material.texId1 < World.WorldTerrainResources.TerrainMaterialDatas.Length;

        private sealed class PreparedNode : IDisposable
        {
            private readonly int id;
            private OctreeNodeData node;
            private NativeArray<VoxelNodeData> samples;
            private NativeArray<VoxelNodeData> originals;
            internal string Description => $"node {id} tree={node.treeGuid:x16} density={node.densitiesId}";
            internal bool Changed { get; private set; }

            internal PreparedNode(int id, OctreeNodeData node)
            {
                this.id = id;
                this.node = node;
                samples = new NativeArray<VoxelNodeData>(OctreeNodeData.DENSITY_COUNT, Allocator.Temp);
            }

            internal void Stage(Vector3 centre, Vector3 normal, float planeOffset, float radius,
                bool fill, VoxelNodeData fillMaterial)
            {
                OctreeNodeResources.CopyDensityDataToArray(node.densitiesId, samples);
                float scale = node.VoxelSize;
                Vector3 relativeCentre = (Vector3)node.position - centre;
                for (int z = 0; z < OctreeNodeData.DENSITY_WIDTH; z++)
                for (int y = 0; y < OctreeNodeData.DENSITY_WIDTH; y++)
                for (int x = 0; x < OctreeNodeData.DENSITY_WIDTH; x++)
                {
                    Vector3 offset = relativeCentre + (Vector3)node.GetPosition(x, y, z);
                    if (offset.sqrMagnitude >= radius * radius) continue;
                    int index = VoxelNodeData.ToDensityId(x, y, z);
                    var original = samples[index];
                    var next = original;
                    float density = TerrainEditMath.Combine(original.Density,
                        (offset.magnitude - radius) / scale, (Vector3.Dot(offset, normal) - planeOffset) / scale, fill);
                    if (fill && original.Density >= 0f && density < 0f)
                        next = fillMaterial;
                    next.Density = density;
                    if (next.density == original.density) continue;
                    if (!Changed) originals = new NativeArray<VoxelNodeData>(samples, Allocator.Temp);
                    samples[index] = next;
                    Changed = true;
                }
            }

            internal void Queue()
            {
                OctreeNodeResources.QueueNodeSave(ref node, id);
                if (node.fadeState == NodeFadeState.fadeIn)
                {
                    OctreeNodeJobs.toGeoCompute.Add(id);
                }
                else
                {
                    node.isGeoDirty = true;
                    OctreeNodeResources.OctreeNodeDatas[id] = node;
                }
            }

            internal void Write() => CopyToResidentSlot(samples);
            internal void Restore() => CopyToResidentSlot(originals);

            private void CopyToResidentSlot(NativeArray<VoxelNodeData> source)
            {
                int densityId = node.densitiesId;
                OctreeNodeResources.SetVoxelData(ref densityId, source);
                if (densityId != node.densitiesId)
                    throw new InvalidOperationException("The resident density slot changed during copy.");
            }

            internal void RefreshProps(Vector3l systemPoint)
            {
                try { PropNodeItems.RemoveGrassAndPlants(systemPoint, ref node); }
                finally { OctreeNodeResources.OctreeNodeDatas[id] = node; }
            }

            public void Dispose()
            {
                if (originals.IsCreated) originals.Dispose();
                samples.Dispose();
            }
        }
    }
}
