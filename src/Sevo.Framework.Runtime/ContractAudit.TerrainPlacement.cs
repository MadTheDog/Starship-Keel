using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string PlacementVoxel = "World.Terrain.VoxelNodeData";
        private const string PlacementNode = "World.Terrain.OctreeNodeData";
        private const string PlacementCompute = "World.Terrain.DensityComputeJob";
        private const string PlacementNodes = "World.Terrain.OctreeNodeResources";
        private const string PlacementReadback = "UnityEngine.Rendering.AsyncGPUReadbackRequest";
        private const string PlacementVoxels = "Unity.Collections.NativeArray`1<" + PlacementVoxel + ">";

        public static CapabilityResult EvaluateTerrainPlacement(ModuleDefinition module)
        {
            const string capability = "terrainPlacement";
            try
            {
                var voxel = module.GetType(PlacementVoxel);
                var fields = voxel?.Fields.Where(field => !field.IsStatic).ToArray();
                if (voxel?.IsSequentialLayout != true || !voxel.IsValueType || fields == null
                    || !fields.Select(field => (field.Name, field.FieldType.FullName)).SequenceEqual(new[]
                    {
                        ("color", "System.UInt32"), ("density", "System.Int16"),
                        ("texId0", "System.Byte"), ("texId1", "System.Byte")
                    }) || fields.Any(field => !field.IsPublic))
                    throw new InvalidOperationException("Packed terrain material and density layout changed.");
                RequireTerrainSoloContext(module);
                if (!Field(module, PlacementNode, "planetGenerationId", "System.Int32", false)
                    || !Field(module, PlacementNode, "asteroidId", "System.Int32", false)
                    || !Field(module, TerrainAsteroidInstance, "seed", "System.UInt64", false)
                    || !Field(module, TerrainBody, "planetGenerationId", "System.Int32", false)
                    || !RequireTerrainProviderMethod(module, TerrainBody, "get_Seed", false, "System.UInt64").IsPublic
                    || !Field(module, "World.PlanetarySystem", "SolarBodies", "System.Collections.Generic.List`1<" + TerrainBody + ">", true)
                    || !Field(module, "World.Terrain.AsteroidResources", "datas", "Common.NativeRecyclableContainer`1<" + TerrainAsteroidInstance + ">", true)
                    || !Field(module, MaterialResources, "TerrainMaterialDatas", MaterialArray, true)
                    || !Field(module, MaterialResources, "terrainTextures", "System.Collections.Generic.List`1<" + MaterialResources + "/TerrainTexture>", false)
                    || !Field(module, MaterialResources + "/TerrainTexture", "ShaderName", "System.String", false))
                    throw new InvalidOperationException("Terrain placement owner or material reference changed.");
                var constructor = RequireTerrainProviderMethod(module, PlacementCompute, ".ctor", false, TerrainVoid);
                var gpuBuffer = constructor.Body.Instructions.FirstOrDefault(instruction => instruction.OpCode.Code == Code.Newobj
                    && instruction.Operand is MethodReference method && method.DeclaringType.FullName == "UnityEngine.ComputeBuffer");
                if (gpuBuffer?.Previous?.Previous == null || Constant(gpuBuffer.Previous) != 8
                    || Constant(gpuBuffer.Previous.Previous) != TerrainPlacement.VoxelCount)
                    throw new InvalidOperationException("Terrain density GPU buffer size or stride changed.");

                var readback = RequireTerrainProviderMethod(module, PlacementCompute, "OnReadbackFinished", false, TerrainVoid, PlacementReadback);
                if (!readback.Body.Instructions.Any(instruction => TerrainPlacementCall(instruction, PlacementReadback, "get_hasError"))
                    || !readback.Body.Instructions.Any(instruction => TerrainProviderField(instruction, Code.Stfld, PlacementCompute, "isCompleted")))
                    throw new InvalidOperationException("Native async terrain readback callback changed.");
                var publish = RequireTerrainProviderMethod(module, PlacementCompute, "CheckForFinishedJobs", true, TerrainVoid);
                var instructions = publish.Body.Instructions;
                var copy = instructions.SingleOrDefault(instruction => TerrainPlacementCall(instruction, PlacementNodes, "SetVoxelData"));
                var ordinal = copy == null ? -1 : instructions.IndexOf(copy);
                if (ordinal < 6 || publish.Body.Variables.Count < 3
                    || publish.Body.Variables[1].VariableType.FullName != PlacementCompute
                    || publish.Body.Variables[2].VariableType.FullName != PlacementNode
                    || !TerrainProviderField(instructions[ordinal - 1], Code.Ldfld, PlacementCompute, "voxelNodeDatas")
                    || instructions[ordinal - 2].OpCode.Code != Code.Ldloc_1
                    || !TerrainProviderField(instructions[ordinal - 3], Code.Ldflda, PlacementNode, "densitiesId")
                    || instructions[ordinal - 4].OpCode.Code != Code.Ldloca_S
                    || instructions[ordinal - 4].Operand is not VariableDefinition local || local.Index != 2
                    || instructions[ordinal - 5].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || instructions[ordinal - 5].Operand is not Instruction invalidNode || instructions.IndexOf(invalidNode) <= ordinal
                    || !TerrainPlacementCall(instructions[ordinal - 6], PlacementCompute + "/JobNodeData", "IsValid")
                    || !instructions.Skip(ordinal + 1).Any(instruction => TerrainProviderField(instruction, Code.Ldsfld,
                        "World.Terrain.OctreeNodeJobs", "toGeoCompute")))
                    throw new InvalidOperationException("Native valid-node readback publication changed.");
                var copyMethod = RequireTerrainProviderMethod(module, PlacementNodes, "SetVoxelData", true, TerrainVoid,
                    "System.Int32&", PlacementVoxels);
                if (!copyMethod.IsPublic)
                    throw new InvalidOperationException("Native terrain copy is unavailable.");

                var immediate = RequireTerrainProviderMethod(module, PlacementCompute, "DispatchImmediate", false, TerrainVoid,
                    PlacementNode, TerrainAsteroidInstance, PlacementVoxel + "[]");
                var syncReadback = immediate.Body.Instructions.SingleOrDefault(instruction =>
                    TerrainPlacementCall(instruction, "UnityEngine.ComputeBuffer", "GetData"));
                if (syncReadback == null || immediate.Body.Instructions.Count(instruction => instruction.OpCode.Code == Code.Ret) != 1
                    || immediate.Body.Instructions.Any(instruction => instruction.Offset < syncReadback.Offset
                        && instruction.Operand is Instruction target && target.Offset > syncReadback.Offset)
                    || !immediate.Body.Instructions.TakeWhile(instruction => instruction != syncReadback)
                    .Any(instruction => TerrainPlacementCall(instruction, "UnityEngine.ComputeShader", "Dispatch")))
                    throw new InvalidOperationException("Synchronous asteroid density readback changed.");
                var bake = RequireTerrainProviderMethod(module, "World.AsteroidFieldResources", "BakeAsteroidLOD", true, "System.Int32",
                    TerrainAsteroidInstance, "System.Int32", "System.Int32", "System.Boolean", "World.PlanetaryRingData");
                int bakeCall = TerrainProviderCall(bake, immediate);
                if (bakeCall < 0 || !bake.Body.Instructions.Skip(bakeCall + 1)
                    .Any(instruction => TerrainPlacementCall(instruction, PlacementVoxels, "CopyFrom")))
                    throw new InvalidOperationException("Native baked asteroid LOD handoff changed.");
                return new CapabilityResult(capability, true, "Packed terrain material remap and generated async and baked asteroid readback seams validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool TerrainPlacementCall(Instruction instruction, string type, string name)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name;
    }
}
