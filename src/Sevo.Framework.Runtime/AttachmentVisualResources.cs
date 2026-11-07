using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal static class AttachmentVisualResources
    {
        private const int MaxVertices = 4096;
        internal const int MaxTriangles = 2048;
        private const float UvInset = 0.0001f;
        [ThreadStatic] private static Binding? active;
        private static FieldInfo? colliderMesh;

        internal static Mesh Prepare(WeaponAttachmentVisual visual, Brick template, string key)
        {
            var mesh = visual.Mesh;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount is < 3 or > MaxVertices || mesh.subMeshCount != 1
                || mesh.GetTopology(0) != MeshTopology.Triangles || mesh.GetIndexCount(0) is < 3 or > MaxTriangles * 3)
                throw new InvalidOperationException("Expected a readable triangle mesh with one submesh within the vertex and triangle limits.");

            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uv = mesh.uv;
            var uv2 = mesh.uv2;
            var colors = mesh.colors32;
            var indices = mesh.triangles;
            var slots = visual.TriangleMaterialSlots;
            if (normals.Length != positions.Length || tangents.Length != positions.Length || uv.Length != positions.Length
                || uv2.Length != 0 && uv2.Length != positions.Length || colors.Length != 0 && colors.Length != positions.Length
                || indices.Length % 3 != 0 || slots.Count != indices.Length / 3)
                throw new InvalidOperationException("Mesh channels or per-triangle material slots are incomplete.");

            var bounds = template.VoxelMesh.Mesh.bounds;
            var toleratedBounds = bounds;
            toleratedBounds.Expand(0.0002f);
            for (int index = 0; index < positions.Length; index++)
            {
                var tangent = tangents[index];
                if (!Finite(positions[index]) || !toleratedBounds.Contains(positions[index])
                    || !Finite(normals[index]) || !UnitLength(normals[index].sqrMagnitude)
                    || !Finite(new Vector3(tangent.x,tangent.y,tangent.z))
                    || !UnitLength(tangent.x*tangent.x+tangent.y*tangent.y+tangent.z*tangent.z)
                    || tangent.w != -1f && tangent.w != 1f || !NormalizedUv(uv[index])
                    || uv2.Length != 0 && (!Finite(uv2[index].x) || !Finite(uv2[index].y)))
                    throw new InvalidOperationException("A vertex has invalid channels or exceeds the stock render envelope.");
            }

            var materials = template.VoxelMaterials;
            var baked = VoxelMaterialResources.materialParamDatas;
            for (int triangle = 0; triangle < slots.Count; triangle++)
            {
                int start = triangle * 3;
                int a = indices[start], b = indices[start+1], c = indices[start+2];
                if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length
                    || Vector3.Cross(positions[b]-positions[a],positions[c]-positions[a]).sqrMagnitude <= 1e-16f)
                    throw new InvalidOperationException("A triangle has invalid indices or zero area.");
                int slot = slots[triangle];
                if ((uint)slot >= materials.Length || !IsStaticOpaque(materials[slot],baked))
                    throw new InvalidOperationException("A triangle requests an unavailable or unsupported baked opaque material slot.");
            }

            var vertices = new List<Vector3>(positions.Length);
            var newNormals = new List<Vector3>(positions.Length);
            var newTangents = new List<Vector4>(positions.Length);
            var newUv = new List<Vector2>(positions.Length);
            var newUv2 = new List<Vector2>(positions.Length);
            var newColors = new List<Color32>(positions.Length);
            var verticesBySlot = new Dictionary<(int vertex,int slot),int>();
            for (int corner = 0; corner < indices.Length; corner++)
            {
                int index = indices[corner];
                int slot = slots[corner/3];
                if (!verticesBySlot.TryGetValue((index,slot),out int encoded))
                {
                    encoded = vertices.Count;
                    verticesBySlot.Add((index,slot),encoded);
                    vertices.Add(positions[index]);
                    newNormals.Add(normals[index]);
                    newTangents.Add(tangents[index]);
                    newUv.Add(new Vector2(slot+Mathf.Lerp(UvInset,1f-UvInset,uv[index].x),uv[index].y));
                    newUv2.Add(uv2.Length==0 ? Vector2.zero : uv2[index]);
                    newColors.Add(colors.Length==0 ? new Color32(255,255,255,255) : colors[index]);
                }
                indices[corner] = encoded;
            }

            var owned = new Mesh { name = "Keel attachment visual " + key };
            try
            {
                owned.SetVertices(vertices);
                owned.SetNormals(newNormals);
                owned.SetTangents(newTangents);
                owned.SetUVs(0,newUv);
                owned.SetUVs(1,newUv2);
                owned.SetColors(newColors);
                owned.SetTriangles(indices,0,calculateBounds:false);
                // The native collider fallback and BrickProperties both consume these bounds.
                owned.bounds = bounds;
                return owned;
            }
            catch
            {
                UnityEngine.Object.Destroy(owned);
                throw;
            }
        }

        private static bool IsStaticOpaque(VoxelMaterial material,VoxelMaterial[] baked) =>
            (uint)material.Id < baked.Length && baked[material.Id].Id==material.Id
            && material.dynDataVert==DynData_Vert.none && material.dynDataPixel==DynData_Pixel.none
            && material.AlphaMode is AlphaMode.noAlpha or AlphaMode.glowPaint or AlphaMode.glowDiffuse;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool UnitLength(float square) => square >= 0.98f && square <= 1.02f;
        private static bool NormalizedUv(Vector2 uv) => uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

        internal static void Install(Harmony harmony)
        {
            colliderMesh = AccessTools.Field(typeof(VoxelItemCollider),"Mesh")
                ?? throw new InvalidOperationException("The native collider mesh field is unavailable.");
            var constructor = typeof(VoxelMesh).GetConstructor(new[]
            {
                typeof(string),typeof(Brick),typeof(float),typeof(ResizeMode),typeof(bool),typeof(bool)
            }) ?? throw new InvalidOperationException("The native mesh constructor is unavailable.");
            harmony.Patch(constructor,transpiler:new HarmonyMethod(typeof(AttachmentVisualResources),nameof(SubstituteRenderMeshes)));
        }

        private static IEnumerable<CodeInstruction> SubstituteRenderMeshes(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            var main = AccessTools.Field(typeof(VoxelMesh),nameof(VoxelMesh.Mesh));
            var preview = AccessTools.Field(typeof(VoxelMesh),nameof(VoxelMesh.MeshPreview));
            var mainStores = body.Where(i=>i.opcode==OpCodes.Stfld && Equals(i.operand,main)).ToArray();
            var previewStores = body.Where(i=>i.opcode==OpCodes.Stfld && Equals(i.operand,preview)).ToArray();
            if (mainStores.Length!=1 || previewStores.Length!=2 || body.IndexOf(mainStores[0])>=body.IndexOf(previewStores[0]))
                throw new InvalidOperationException("The live native mesh stores are incompatible.");

            InsertBeforeStore(body,previewStores[0],nameof(SelectPreview));
            InsertBeforeStore(body,mainStores[0],nameof(SelectMesh));
            return body;
        }

        private static void InsertBeforeStore(List<CodeInstruction> body,CodeInstruction store,string selector)
        {
            // Harmony's label list exposes a Mono mscorlib type outside the runtime's netstandard surface.
            var labels = AccessTools.Field(typeof(CodeInstruction),"labels")?.GetValue(store) as System.Collections.ICollection;
            if (labels?.Count!=0 || store.blocks.Count!=0)
                throw new InvalidOperationException("Native control flow enters a mesh store directly.");
            int index = body.IndexOf(store);
            body.Insert(index,new CodeInstruction(OpCodes.Ldarg_2));
            body.Insert(index+1,new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(AttachmentVisualResources),selector)));
        }

        private static Mesh? SelectMesh(Mesh? native,Brick owner) =>
            active is { } binding && ReferenceEquals(binding.Brick,owner) ? binding.Mesh : native;
        private static Mesh? SelectPreview(Mesh? native,Brick owner) =>
            active is { } binding && ReferenceEquals(binding.Brick,owner) ? null : native;

        internal static void Load(Brick brick,Brick template,Mesh mesh)
        {
            if (active!=null) throw new InvalidOperationException("Managed mesh resource loads cannot be nested.");
            active = new Binding(brick,mesh);
            try { StockTemplateResources.Load(brick,template); }
            finally { active = null; }

            if (!ReferenceEquals(brick.VoxelMesh.Mesh,mesh) || brick.VoxelMesh.MeshPreview==null
                || ReferenceEquals(brick.VoxelMesh.MeshPreview,mesh)
                || colliderMesh==null || !ReferenceEquals(colliderMesh.GetValue(brick.VoxelCollider),colliderMesh.GetValue(template.VoxelCollider))
                || !brick.VoxelCollider.bounds.Equals(template.VoxelCollider.bounds))
                throw new InvalidOperationException("Native loading did not preserve the managed render and stock collider contracts.");
        }

        private sealed class Binding
        {
            internal Binding(Brick brick,Mesh mesh) { Brick=brick; Mesh=mesh; }
            internal Brick Brick { get; }
            internal Mesh Mesh { get; }
        }
    }
}
