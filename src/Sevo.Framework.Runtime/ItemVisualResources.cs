using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Common;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    internal static class ItemVisualResources
    {
        private const string BufferName = "WorldPropMeshDataBuffer";
        private const int ClusterVertices = 42;
        private static readonly FieldInfo BufferField =
            AccessTools.DeclaredField(typeof(WorldPropsResources), "propMeshDataBuffer");
        private static readonly Dictionary<int, (Color32 Tint, ItemSurfaceMode Surface)> Tints =
            new Dictionary<int, (Color32, ItemSurfaceMode)>();
        private static ComputeBuffer? originalBuffer;
        private static ComputeBuffer? expandedBuffer;
        private static PropMeshData? tintedMaterial;
        private static bool tintedSupported;

        internal static bool Enabled { get; private set; }

        internal static void Install(Harmony harmony, bool allowTinted)
        {
            var render = AccessTools.DeclaredMethod(typeof(ItemInstanceData), nameof(ItemInstanceData.SetRenderData),
                new[] { typeof(EvoRenderer), typeof(float), typeof(int), typeof(bool) });
            harmony.Patch(render, prefix: new HarmonyMethod(typeof(ItemVisualResources), nameof(ApplyTint)),
                finalizer: new HarmonyMethod(typeof(ItemVisualResources), nameof(RestoreTint)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(WorldPropsResources), nameof(WorldPropsResources.OnApplicationQuit)),
                prefix: new HarmonyMethod(typeof(ItemVisualResources), nameof(ReleaseBuffer)));
            tintedSupported = allowTinted;
            Enabled = true;
        }

        internal static void Disable()
        {
            Enabled = false;
        }

        internal sealed class MeshRow
        {
            internal readonly MeshBuffer Mesh;
            internal readonly Aabb RenderBounds;
            internal readonly Aabb ColliderBounds;
            internal readonly Color32 Tint;
            internal readonly ItemSurfaceMode Surface;
            internal readonly PropMeshData? MaterialOverride;
            internal int FirstTriangle = -1;

            internal MeshRow(ItemVisualDefinition visual)
            {
                var mesh = visual.Mesh;
                if (!mesh.isReadable || mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles
                    || mesh.vertexCount < 3 || mesh.vertexCount > 4096 || mesh.GetIndexCount(0) is < 3 or > 12288)
                    throw new ArgumentException("Supply one readable triangle submesh with up to 4,096 vertices and 4,096 triangles.");

                Mesh = new MeshBuffer(mesh);
                var bounds = new Bounds(Mesh.vertices[0], Vector3.zero);
                foreach (var vertex in Mesh.vertices)
                {
                    ValidateVector(vertex, 16f);
                    bounds.Encapsulate(vertex);
                }
                if (bounds.size.sqrMagnitude < 0.000025f)
                    throw new ArgumentException("Item mesh must span at least 5 mm.");
                RenderBounds = new Aabb(bounds);

                var box = visual.ColliderBox;
                ValidateVector(box.min, 16f);
                ValidateVector(box.max, 16f);
                if (box.size.x < 0.005f || box.size.y < 0.005f || box.size.z < 0.005f)
                    throw new ArgumentException("Each box collider dimension must be at least 5 mm.");
                ColliderBounds = new Aabb(box);
                Tint = visual.Tint;
                Surface = visual.Surface;
                if (Surface == ItemSurfaceMode.Tinted) MaterialOverride = ReadTintedMaterial();

                if (Mesh.normals.Count != mesh.vertexCount || Mesh.tangents.Count != mesh.vertexCount
                    || Mesh.uv.Count != mesh.vertexCount)
                    throw new ArgumentException("Supply finite normals, tangents and UV0 for each item mesh vertex.");
                foreach (var normal in Mesh.normals) ValidateVector(normal, 1.001f);
                foreach (var tangent in Mesh.tangents) ValidateVector(new Vector3(tangent.x, tangent.y, tangent.z), 1.001f);
                foreach (var uv in Mesh.uv) ValidateVector(new Vector3(uv.x, uv.y, 0f), 128f);
            }

            private static void ValidateVector(Vector3 value, float limit)
            {
                if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)
                    || Mathf.Abs(value.x) > limit || Mathf.Abs(value.y) > limit || Mathf.Abs(value.z) > limit)
                    throw new ArgumentException("Item mesh or collider contains an invalid coordinate.");
            }

            internal int PaddedVertexCount => ((Mesh.triangles.Count + ClusterVertices - 1) / ClusterVertices) * ClusterVertices;

            internal PrefabData Apply(PrefabData stock)
            {
                stock.renderData.clusterStart = FirstTriangle;
                stock.renderData.clusterCount = PaddedVertexCount / ClusterVertices;
                stock.renderData.clusterStartLOD = 0;
                stock.renderData.clusterCountLOD = 0;
                stock.renderData.bounds = RenderBounds;
                if (MaterialOverride.HasValue) stock.renderData.pass = RenderablePass.worldItemOpaque;
                stock.renderData2 = default;
                stock.radius = RenderBounds.Radius;
                stock.colliderBounds = ColliderBounds;
                stock.useColliderBounds = true;
                stock.shape = ColliderShapeCommon.Shape.box;
                stock.triangleTreeId = 0;
                stock.collisionPointsTreeId = -1;
                return stock;
            }
        }

        private static PropMeshData ReadTintedMaterial()
        {
            if (!tintedSupported) throw new InvalidOperationException("The native tinted item surface contract is unavailable.");
            if (tintedMaterial.HasValue) return tintedMaterial.Value;
            var buffer = BufferField.GetValue(null) as ComputeBuffer;
            var shaderMaterial = AssetRepoStarSystem.Mat_WorldItem;
            var prefabs = ItemInstanceRenderResources.PrefabDatas;
            var donorId = (int)WorldPropCommon.WorldPropId.rockSmallA;
            if (buffer == null || !buffer.IsValid() || buffer.stride != Marshal.SizeOf<PropMeshData>()
                || !prefabs.IsCreated || donorId <= 0 || donorId >= prefabs.Length
                || shaderMaterial == null || shaderMaterial.shader == null || !shaderMaterial.shader.isSupported
                || shaderMaterial.shader.name != "Custom/WorldItemIndirect"
                || shaderMaterial.passCount <= (int)WorldItemShaderPass.forward)
                throw new InvalidOperationException("The live tinted world-item buffer or material is unavailable.");
            var donor = WorldPropsResources.worldPropsIndex[donorId];
            var render = prefabs[donorId].renderData;
            if (donor == null || donor.Id != WorldPropCommon.WorldPropId.rockSmallA || !render.IsValid
                || render.clusterStart < 0 || 3L * render.clusterStart + ClusterVertices > buffer.count)
                throw new InvalidOperationException("The native rock tint surface no longer has a valid world-item row.");
            var row = new PropMeshData[1];
            buffer.GetData(row, 0, render.clusterStart * 3, 1);
            if (row[0].materialType != WorldPropRenderType.rock)
                throw new InvalidOperationException("The native rock tint source changed material mode to " + row[0].materialType + ".");
            tintedMaterial = row[0];
            return row[0];
        }

        internal static void Bind(IReadOnlyList<(int Id, MeshRow Mesh, PrefabData Stock)> visuals)
        {
            if (visuals.Count == 0) return;
            var live = BufferField.GetValue(null) as ComputeBuffer;
            if (expandedBuffer != null)
            {
                if (!ReferenceEquals(live, expandedBuffer) || !expandedBuffer.IsValid())
                    throw new InvalidOperationException("The expanded item mesh buffer lost ownership.");
                return;
            }

            if (live == null || !live.IsValid() || live.stride != Marshal.SizeOf<PropMeshData>()
                || live.count == 0 || live.count % ClusterVertices != 0)
                throw new InvalidOperationException("The stock item mesh buffer is incompatible.");
            int count = live.count;
            foreach (var row in visuals) count = checked(count + row.Mesh.PaddedVertexCount);
            var vertices = new PropMeshData[count];
            live.GetData(vertices, 0, 0, live.count);

            var cursor = live.count;
            foreach (var row in visuals)
            {
                var material = row.Mesh.MaterialOverride ?? vertices[checked(row.Stock.renderData.clusterStart * 3)];
                var tex = new WorldProp.TextureIds
                {
                    textureId = material.textureID,
                    specularTexId = material.specularTexId,
                    normalTexId = material.normalTexId
                };
                row.Mesh.FirstTriangle = cursor / 3;
                for (var index = 0; index < row.Mesh.Mesh.triangles.Count; index++)
                    vertices[cursor + index] = new PropMeshData(row.Mesh.Mesh, index, tex, Vector3.zero, false,
                        material.materialType, 0f, material.subMaterialType, false);
                cursor += row.Mesh.PaddedVertexCount;
            }

            var replacement = new ComputeBuffer(count, live.stride);
            try
            {
                replacement.SetData(vertices);
                BufferField.SetValue(null, replacement);
                Shader.SetGlobalBuffer(BufferName, replacement);
            }
            catch
            {
                BufferField.SetValue(null, live);
                Shader.SetGlobalBuffer(BufferName, live);
                replacement.Dispose();
                throw;
            }
            originalBuffer = live;
            expandedBuffer = replacement;
            foreach (var row in visuals) Tints.Add(row.Id, (row.Mesh.Tint, row.Mesh.Surface));
        }

        private static void ApplyTint(ref ItemInstanceData __instance, out (Color32 Primary, Color32 Secondary)? __state)
        {
            __state = null;
            if (__instance.itemType == WorldItemType.craftingParts
                && Tints.TryGetValue(__instance.prefabId, out var style) && ItemResources.Owns(__instance.prefabId))
            {
                __state = (__instance.color, __instance.colorSec);
                __instance.color = style.Tint;
                if (style.Surface == ItemSurfaceMode.Tinted) __instance.colorSec = style.Tint;
            }
        }

        private static void RestoreTint(ref ItemInstanceData __instance, (Color32 Primary, Color32 Secondary)? __state)
        {
            if (!__state.HasValue) return;
            __instance.color = __state.Value.Primary;
            __instance.colorSec = __state.Value.Secondary;
        }

        private static void ReleaseBuffer()
        {
            if (expandedBuffer == null) return;
            // Stock owns the live buffer at quit. Dispose only the original buffer we retained.
            originalBuffer?.Dispose();
            originalBuffer = null;
            expandedBuffer = null;
            tintedMaterial = null;
            Tints.Clear();
        }
    }
}
