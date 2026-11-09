using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateWorldSurfaceQuery(ModuleDefinition module)
        {
            const string capability = "worldSurfaceQuery";
            const string ray = "VoxelEngine.VoxelRaycast";
            const string hit = "VoxelEngine.VoxelRaycastHit";
            const string loop = "Manager.GameloopManager";
            const string player = "PlayerState";
            const string vector = "UnityEngine.Vector3";
            const string flag = "System.Boolean";
            try
            {
                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters)
                    => Method(module, type, name, isStatic, result, parameters) is { IsPublic: true } method ? method
                        : throw new InvalidOperationException("Missing native surface query method: " + type + "." + name);

                Require(loop, "get_IsMainThread", true, flag);
                Require(loop, "get_IsNativeContainerAccessUnsafe", true, flag);
                Require(player, "get_InLoadingScreen", true, flag);
                Require(player, "get_IsQuittingToDesktopOrTitle", true, flag);
                Require(player, "get_sessionMode", true, player + "/SessionMode");
                Require(ray, ".ctor", false, "System.Void");
                Require(hit, "get_WorldNormal", false, vector);
                var query = Require(ray, "Raycast", false, flag, vector, vector, hit + "&",
                    "System.Single", "VoxelEngine.ParentEntity", "System.Int32");
                if (!Field(module, ray, "includeWater", flag, false)
                    || !Field(module, hit, "type", hit + "/Type", false)
                    || !Field(module, hit, "distance", "System.Single", false))
                    throw new InvalidOperationException("Native surface query fields changed.");

                RequireSurfaceEnum(module, hit + "/Type", "voxel", (int)VoxelRaycastHit.Type.voxel);
                RequireSurfaceEnum(module, hit + "/Type", "terrain", (int)VoxelRaycastHit.Type.terrain);
                RequireSurfaceEnum(module, hit + "/Type", "newTerrain", (int)VoxelRaycastHit.Type.newTerrain);
                RequireSurfaceEnum(module, hit + "/Type", "water", (int)VoxelRaycastHit.Type.water);
                RequireSurfaceEnum(module, player + "/SessionMode", "solo", (int)PlayerState.SessionMode.solo);

                const string physicsRay = "Manager.SkyPhysics/QueryRaycast";
                var execute = Require(physicsRay, "Execute", false, flag, hit + "&");
                bool oceanCalled = execute.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Call
                    && instruction.Operand is MethodReference call && call.DeclaringType.FullName == "World.Terrain.Planet.PlanetCommon"
                    && call.Name == "IntersectOcean" && call.ReturnType.FullName == flag
                    && call.Parameters.Count == 4 && call.Parameters[2].ParameterType.FullName == hit + "&");
                var body = query.Body.Instructions;
                bool waterForwarded = body.Any(instruction =>
                    SurfaceField(instruction, Code.Ldfld, ray, "includeWater", flag)
                    && instruction.Next != null && instruction.Next.Operand is FieldReference target
                    && target.DeclaringType.FullName == physicsRay
                    && SurfaceField(instruction.Next, Code.Stfld, target.DeclaringType.FullName, "includeWater", flag));
                bool physicsCalled = body.Any(instruction => instruction.OpCode.Code == Code.Call
                    && instruction.Operand is MethodReference call && call.DeclaringType.FullName == physicsRay
                    && call.Name == "Execute" && call.ReturnType.FullName == flag
                    && call.Parameters.Count == 1 && call.Parameters[0].ParameterType.FullName == hit + "&");
                if (!waterForwarded || !physicsCalled || !oceanCalled)
                    throw new InvalidOperationException("Native world raycast no longer forwards water to the expected physics query.");

                return new CapabilityResult(capability, true, "Validated native safe-phase terrain, grid, and water raycast contracts.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool SurfaceField(Instruction instruction, Code code, string type, string field, string fieldType)
            => instruction.OpCode.Code == code && instruction.Operand is FieldReference reference
                && reference.Name == field && reference.DeclaringType.FullName == type && reference.FieldType.FullName == fieldType;

        private static void RequireSurfaceEnum(ModuleDefinition module, string type, string name, int expected)
        {
            var field = module.GetType(type)?.Fields.SingleOrDefault(candidate => candidate.Name == name);
            if (field?.HasConstant != true || field.Constant == null || Convert.ToInt32(field.Constant) != expected)
                throw new InvalidOperationException("Native surface query enum changed: " + type + "." + name);
        }
    }
}
