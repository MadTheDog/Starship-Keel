using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateItemVisuals(ModuleDefinition module)
        {
            const string capability = "itemVisuals";
            try
            {
                var preview = RequireItemMethod(module, "VoxelEngine.ItemPreview", "ComputeGeoItem", false, "System.Void");
                var allocate = RequireItemMethod(module, ItemDataType, "AllocateAllComponents", false, "System.Void",
                    "Manager.SkyTransformData", "System.Boolean", "System.Boolean", "System.Single", "System.Int32", "System.Boolean");
                var render = RequireItemMethod(module, ItemDataType, "SetRenderData", false, "System.Void",
                    "VoxelEngine.EvoRenderer", "System.Single", "System.Int32", "System.Boolean");
                var collider = RequireItemMethod(module, ItemDataType, "AllocateCollider", false, "System.Void", "System.Boolean");
                var bounds = RequireItemMethod(module, PrefabDataType, "get_GetColliderBounds", false, "Common.Aabb");
                if (!CallsOnce(preview, render) || !CallsOnce(allocate, render) || !CallsOnce(allocate, collider)
                    || !CallsOnce(collider, bounds) || !ItemFieldAccess(render, ItemDataType, "color")
                    || !ItemFieldAccess(render, PrefabDataType, "renderData")
                    || !ItemFieldAccess(collider, PrefabDataType, "shape")
                    || !ItemFieldAccess(collider, PrefabDataType, "triangleTreeId")
                    || !ItemFieldAccess(collider, PrefabDataType, "collisionPointsTreeId")
                    || !ItemFieldAccess(bounds, PrefabDataType, "useColliderBounds")
                    || !ItemFieldAccess(bounds, PrefabDataType, "colliderBounds"))
                    throw new InvalidOperationException("Native item tint or independent collider bounds changed.");

                const string resources = "World.WorldPropsResources";
                var load = RequireItemMethod(module, resources, "LoadResources", false, "System.Void", "System.Boolean");
                var quit = RequireItemMethod(module, resources, "OnApplicationQuit", false, "System.Void");
                if (module.GetType(resources)?.Fields.Count(field => field.IsStatic
                        && field.Name == "propMeshDataBuffer" && field.FieldType.FullName == "UnityEngine.ComputeBuffer") != 1
                    || !ItemFieldAccess(load, resources, "propMeshDataBuffer")
                    || !ItemFieldAccess(quit, resources, "propMeshDataBuffer")
                    || !load.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr
                        && Equals(i.Operand, "WorldPropMeshDataBuffer")))
                    throw new InvalidOperationException("Native world prop GPU buffer ownership changed.");

                var data = module.GetType("World.PropMeshData");
                if (data == null || !data.IsSequentialLayout
                    || !data.Fields.Where(field => !field.IsStatic).Select(field => field.Name + ":" + field.FieldType.FullName)
                        .SequenceEqual(new[] { "position:UnityEngine.Vector3", "normal:UnityEngine.Vector3",
                            "tangent:UnityEngine.Vector4", "binormal:UnityEngine.Vector3", "uv:UnityEngine.Vector2",
                            "textureID:System.Int32", "specularTexId:System.Int32", "normalTexId:System.Int32",
                            "useGrassColor:System.Int32", "materialType:Game.WorldPropRenderType", "subMaterialType:System.Int32" }))
                    throw new InvalidOperationException("Native prop mesh vertex layout changed.");

                return new CapabilityResult(capability, true, "Native item preview, render buffer, tint and box collider contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool CallsOnce(MethodDefinition caller, MethodDefinition target) =>
            caller.Body.Instructions.Count(instruction => (instruction.OpCode.Code == Code.Call
                || instruction.OpCode.Code == Code.Callvirt) && instruction.Operand is MethodReference called
                && called.FullName == target.FullName) == 1;
    }
}
