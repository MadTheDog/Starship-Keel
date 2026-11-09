using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;
using VoxelEngine;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateTerrainEdits(ModuleDefinition module)
        {
            const string capability = "terrainEdits";
            const string resources = "World.Terrain.OctreeNodeResources";
            const string node = "World.Terrain.OctreeNodeData";
            const string sample = "World.Terrain.VoxelNodeData";
            const string jobs = "World.Terrain.OctreeNodeJobs";
            const string hit = "VoxelEngine.VoxelRaycastHit";
            const string transform = "Manager.SkyTransformData";
            const string samples = "Unity.Collections.NativeArray`1<" + sample + ">";
            const string integer = "System.Int32";
            const string flag = "System.Boolean";
            const string nothing = "System.Void";

            try
            {
                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters)
                    => Method(module, type, name, isStatic, result, parameters) is { IsPublic: true } method ? method
                        : throw new InvalidOperationException("Missing terrain edit method: " + type + "." + name);

                var rayAudit = EvaluateWorldSurfaceQuery(module);
                if (!rayAudit.Available) throw new InvalidOperationException(rayAudit.Reason);

                Require(resources, "ShouldSaveNode", true, flag, integer);
                var read = Require(resources, "CopyDensityDataToArray", true, nothing, integer, samples);
                var write = Require(resources, "SetVoxelData", true, nothing, integer + "&", samples);
                var save = Require(resources, "QueueNodeSave", true, nothing, node + "&", integer);
                const string saveRequest = resources + "/NodeSaveRequest";
                const string savePayload = "World.Terrain.NodeSavePayload";
                var buildPayload = Require(saveRequest, "BuildPayload", false, nothing, savePayload);
                var writePayload = Require(saveRequest, "WritePayloadToDisk", false, nothing);
                var flush = Require(resources, "FlushPendingNodeSaves", true, nothing, "System.String");
                var dispatch = Method(module, resources, "BuildAndScheduleNodeSave", true, nothing, saveRequest)
                    ?? throw new InvalidOperationException("Native terrain save dispatch is missing.");
                var traverse = Require(node, "GetOverlappingNodes", false, nothing, "UnityEngine.Vector3",
                    "Unity.Collections.NativeList`1<System.Int32>", integer, "System.Single");
                var removeProps = Require("World.PropNodeItems", "RemoveGrassAndPlants", true, nothing, "Vector3l", node + "&");
                Require(node, "get_DensityBoundsTight", false, "UnityEngine.Bounds");
                Require(node, "get_VoxelSize", false, "System.Single");
                Require(node, "GetPosition", false, "Unity.Mathematics.float3", integer, integer, integer);
                Require(sample, "ToDensityId", true, integer, integer, integer, integer);
                Require(sample, "get_Density", false, "System.Single");
                var compress = Require(sample, "CompressDensity", true, "System.Int16", "System.Single");
                var setDensity = Require(sample, "set_Density", false, nothing, "System.Single");
                Require(hit, "get_TransformData", false, transform);
                Require(hit, "get_SystemPosition", false, "Vector3l");
                RequireSurfaceEnum(module, node, "DENSITY_WIDTH", OctreeNodeData.DENSITY_WIDTH);
                RequireSurfaceEnum(module, node, "DENSITY_HW", OctreeNodeData.DENSITY_HW);
                RequireSurfaceEnum(module, node, "DENSITY_COUNT", OctreeNodeData.DENSITY_COUNT);

                var stockImpact = Require(resources, "OnImpact", true, nothing, "Manager.ImpactData");
                var edit = Method(module, resources, "UpdateDensitiesOnNode", true, nothing,
                    integer, "Unity.Mathematics.float3", "Manager.ImpactData", "Vector3l");
                bool Calls(MethodDefinition caller, MethodDefinition target) => caller.HasBody &&
                    caller.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                        && instruction.Operand is MethodReference called && called.FullName == target.FullName);
                bool CallsNamed(MethodDefinition caller, string name) => caller.HasBody &&
                    caller.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                        && instruction.Operand is MethodReference called && called.Name == name);
                if (edit == null || !Calls(stockImpact, edit) || !Calls(stockImpact, traverse) || !Calls(setDensity, compress)
                    || !Calls(edit, save) || !Calls(edit, removeProps)
                    || !CallsNamed(read, "CopyToSameChunk") || !CallsNamed(write, "CopyFromSameChunk"))
                    throw new InvalidOperationException("The native terrain edit, geometry, or persistence path changed.");

                if (!dispatch.HasBody)
                    throw new InvalidOperationException("Native terrain save dispatch has no managed body.");
                int buildIndex = -1;
                int taskIndex = -1;
                for (int i = 0; i < dispatch.Body.Instructions.Count; i++)
                {
                    var instruction = dispatch.Body.Instructions[i];
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
                        || instruction.Operand is not MethodReference called) continue;
                    if (called.FullName == buildPayload.FullName) buildIndex = i;
                    if (called.DeclaringType.FullName == "System.Threading.Tasks.Task" && called.Name == "Run") taskIndex = i;
                }

                if (!Calls(flush, dispatch) || buildIndex < 0 || taskIndex <= buildIndex
                    || !CallsNamed(buildPayload, "CopyToSameChunk")
                    || !buildPayload.Body.Instructions.Any(instruction =>
                        SurfaceField(instruction, Code.Ldfld, node, "densitiesId", integer))
                    || !dispatch.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldftn
                        && instruction.Operand is MethodReference called && called.FullName == writePayload.FullName)
                    || !save.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Newobj
                        && instruction.Operand is MethodReference called && called.DeclaringType.FullName == saveRequest)
                    || !CallsNamed(save, "set_Item")
                    || Calls(save, dispatch) || Calls(save, buildPayload) || Calls(save, writePayload)
                    || CallsNamed(save, "CopyToSameChunk") || CallsNamed(save, "Run"))
                    throw new InvalidOperationException("Native terrain save queue no longer defers its live density copy until flush.");

                string geoState = module.GetType(node)?.Fields.SingleOrDefault(field => field.Name == "geoState")?.FieldType.FullName ?? "";
                string fadeState = module.GetType(node)?.Fields.SingleOrDefault(field => field.Name == "fadeState")?.FieldType.FullName ?? "";
                RequireSurfaceEnum(module, geoState, "hasGeo", (int)NodeGeoState.hasGeo);
                RequireSurfaceEnum(module, fadeState, "fadeIn", (int)NodeFadeState.fadeIn);
                if (!Field(module, jobs, "toGeoCompute", "System.Collections.Generic.List`1<System.Int32>", true)
                    || !edit.Body.Instructions.Any(instruction =>
                        SurfaceField(instruction, Code.Ldsfld, jobs, "toGeoCompute", "System.Collections.Generic.List`1<System.Int32>"))
                    || !edit.Body.Instructions.Any(instruction => SurfaceField(instruction, Code.Stfld, node, "isGeoDirty", flag))
                    || !Field(module, resources, "saveParsed", flag, true)
                    || !Field(module, node, "position", "Unity.Mathematics.float3", false)
                    || !Field(module, node, "transformId", integer, false)
                    || !Field(module, node, "ultimateParentId", integer, false)
                    || !Field(module, node, "planetGenerationId", integer, false)
                    || !Field(module, node, "asteroidId", integer, false)
                    || !Field(module, node, "densitiesId", integer, false)
                    || !Field(module, node, "treeGuid", "System.UInt64", false)
                    || !Field(module, node, "densityComputed", flag, false)
                    || !Field(module, node, "isJobStarted", flag, false)
                    || !Field(module, sample, "density", "System.Int16", false)
                    || !Field(module, sample, "texId0", "System.Byte", false)
                    || !Field(module, sample, "texId1", "System.Byte", false)
                    || !Field(module, hit, "elementId", integer, false)
                    || !Field(module, hit, "transformId", integer, false)
                    || !Field(module, hit, "localPoint", "UnityEngine.Vector3", false)
                    || !Field(module, hit, "localNormal", "UnityEngine.Vector3", false)
                    || !Field(module, transform, "rotation", "UnityEngine.Quaternion", false)
                    || !Field(module, transform, "scale", "System.Single", false))
                    throw new InvalidOperationException("Native terrain node or geometry scheduling fields changed.");

                return new CapabilityResult(capability, true,
                    "Validated native resident terrain density, geometry invalidation, and save queue contracts.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
