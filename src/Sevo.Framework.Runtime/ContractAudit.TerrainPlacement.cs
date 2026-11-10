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

        public static CapabilityResult EvaluateTerrainRadialPlacement(ModuleDefinition module)
        {
            const string capability = "terrainRadialPlacement";
            const string float3 = "Unity.Mathematics.float3";
            const string int32 = "System.Int32";
            try
            {
                if (!Field(module, PlacementNode, "position", float3, false))
                    throw new InvalidOperationException("Planet node coordinates changed.");
                var radius = RequireTerrainProviderMethod(module, TerrainBody, "get_radius", false, "System.Single");
                var size = RequireTerrainProviderMethod(module, PlacementNode, "get_VoxelSize", false, "System.Single");
                var position = RequireTerrainProviderMethod(module, PlacementNode, "GetPosition", false, float3, int32, int32, int32);
                var centreCode = position.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                if (!radius.IsPublic || !position.IsPublic || !size.IsPublic || centreCode.Length != 23
                    || centreCode[0].OpCode.Code != Code.Ldarg_0
                    || !TerrainPlacementCall(centreCode[1], PlacementNode, size.Name)
                    || !TerrainPlacementAxis(centreCode, 2, Code.Ldarg_1)
                    || !TerrainPlacementAxis(centreCode, 8, Code.Ldarg_2)
                    || !TerrainPlacementAxis(centreCode, 14, Code.Ldarg_3)
                    || centreCode[20].OpCode.Code != Code.Newobj
                    || centreCode[20].Operand is not MethodReference constructor || constructor.DeclaringType.FullName != float3
                    || !TerrainPlacementCall(centreCode[21], float3, "op_Multiply") || centreCode[22].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native planet voxel centres changed.");

                var index = RequireTerrainProviderMethod(module, PlacementVoxel, "ToDensityId", true, int32, int32, int32, int32);
                var code = index.Body.Instructions;
                if (!code.Select(instruction => instruction.OpCode.Code).SequenceEqual(new[]
                    {
                        Code.Ldarg_0, Code.Ldarg_1, Code.Ldc_I4_S, Code.Mul, Code.Add,
                        Code.Ldarg_2, Code.Ldc_I4_S, Code.Mul, Code.Ldc_I4_S, Code.Mul, Code.Add, Code.Ret
                    }) || Constant(code[2]) != 20 || Constant(code[6]) != 20 || Constant(code[8]) != 20)
                    throw new InvalidOperationException("Native density sample ordering changed.");

                var geometry = module.GetType("World.Terrain.OctreeNodeJobs/Job_GeoCompute")?.Methods
                    .SingleOrDefault(method => method.Name == "GetVertex" && method.HasBody);
                if (geometry == null)
                    throw new InvalidOperationException("Native planet geometry no longer uses body-relative voxel centres.");
                var instructions = geometry.Body.Instructions;
                var nodePosition = instructions.FirstOrDefault(instruction =>
                    TerrainProviderField(instruction, Code.Ldfld, PlacementNode, "position"));
                if (nodePosition == null || !instructions.Skip(instructions.IndexOf(nodePosition) + 1).Take(26)
                    .Any(instruction => TerrainPlacementCall(instruction, "Unity.Mathematics.math", "length"))
                    || !instructions.Skip(instructions.IndexOf(nodePosition) + 1).Take(16)
                    .Any(instruction => TerrainPlacementCall(instruction, PlacementNode, "GetPosition")))
                    throw new InvalidOperationException("Native planet geometry no longer uses body-relative voxel centres.");

                return new CapabilityResult(capability, true, "Native planet radius, voxel centres and density ordering validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateAsteroidDepositCoordinates(ModuleDefinition module)
        {
            const string capability = "asteroidDepositCoordinates";
            try
            {
                var dispatch = RequireTerrainProviderMethod(module, PlacementCompute, "Dispatch", false, TerrainVoid,
                    "System.Int32", PlacementCompute + "/JobNodeData");
                var immediate = RequireTerrainProviderMethod(module, PlacementCompute, "DispatchImmediate", false,
                    TerrainVoid, PlacementNode, TerrainAsteroidInstance, PlacementVoxel + "[]");
                foreach (var method in new[] { dispatch, immediate })
                {
                    var instructions = method.Body.Instructions;
                    var position = instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Ldstr
                        && Equals(instruction.Operand, "_NodePosition"));
                    if (position == null || !instructions.Skip(instructions.IndexOf(position) + 1).Take(4)
                        .Any(instruction => TerrainProviderField(instruction, Code.Ldfld, PlacementNode, "position"))
                        || !instructions.Any(instruction => TerrainPlacementCall(instruction, PlacementNode, "get_VoxelSize"))
                        || !instructions.Any(instruction => TerrainPlacementCall(instruction,
                            TerrainAsteroidInstance, "get_AsteroidDensityGenerationData")))
                        throw new InvalidOperationException("Asteroid dispatch no longer uses the audited node origin and scale.");
                }
                if (!Field(module, TerrainAsteroidInstance, "seed", "System.UInt64", false)
                    || !dispatch.Body.Instructions.Any(instruction => TerrainProviderField(instruction,
                        Code.Ldsflda, "World.Terrain.AsteroidResources", "datas"))
                    || !dispatch.Body.Instructions.Any(instruction => TerrainProviderField(instruction,
                        Code.Ldfld, PlacementNode, "asteroidId")))
                    throw new InvalidOperationException("The native asteroid instance binding changed.");
                return new CapabilityResult(capability, true, "Async and baked asteroid node origins, scales and instance binding validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateTerrainAuthoredSurfacePlacement(ModuleDefinition module)
        {
            const string capability = "terrainAuthoredSurfacePlacement";
            try
            {
                var positions = EvaluateTerrainRadialPlacement(module);
                if (!positions.Available) throw new InvalidOperationException(positions.Reason);
                if (!Field(module, "World.PlanetarySystem", "CurrentStarData", "World.StarData", true)
                    || !Field(module, "World.StarData", "seed", "System.UInt64", false))
                    throw new InvalidOperationException("Planet terrain system identity changed.");
                var density = RequireTerrainProviderMethod(module, PlacementVoxel, "get_Density", false, "System.Single");
                var decode = density.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                if (!density.IsPublic || decode.Length != 6 || decode[0].OpCode.Code != Code.Ldarg_0
                    || !TerrainProviderField(decode[1], Code.Ldfld, PlacementVoxel, "density")
                    || decode[2].OpCode.Code != Code.Conv_R4 || decode[3].OpCode.Code != Code.Ldc_R4
                    || decode[3].Operand is not float scale || !float.IsFinite(scale) || scale <= 0
                    || decode[4].OpCode.Code is not (Code.Div or Code.Mul) || decode[5].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native terrain density no longer preserves the packed sign.");

                var mining = RequireTerrainProviderMethod(module, PlacementNodes, "UpdateDensitiesOnNode", true,
                    TerrainVoid, "System.Int32", "Unity.Mathematics.float3", "Manager.ImpactData", "Vector3l");
                if (!mining.Body.Instructions.Any(instruction =>
                    TerrainPlacementCall(instruction, PlacementVoxel, density.Name)
                    && instruction.Next is { OpCode.Code: Code.Ldc_R4, Operand: 0f }
                    && instruction.Next.Next?.OpCode.Code == Code.Clt))
                    throw new InvalidOperationException("Stock mining no longer identifies solid samples by negative density.");

                return new CapabilityResult(capability, true, "Body-local voxel coordinates and stock solid density polarity validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool TerrainPlacementAxis(Instruction[] code, int start, Code argument)
            => code[start].OpCode.Code == argument
                && (code[start + 1].OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S) && Constant(code[start + 1]) == 10
                && code[start + 2].OpCode.Code == Code.Sub && code[start + 3].OpCode.Code == Code.Conv_R4
                && code[start + 4].OpCode.Code == Code.Ldc_R4 && Equals(code[start + 4].Operand, 0.5f)
                && code[start + 5].OpCode.Code == Code.Add;

        private static bool TerrainPlacementCall(Instruction instruction, string type, string name)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name;
    }
}
