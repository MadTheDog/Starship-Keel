using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateTerrainDepositScanner(ModuleDefinition module)
        {
            const string capability = "terrainDepositScanner";
            const string scanner = "World.Terrain.OreScannerResources";
            const string node = "World.Terrain.OctreeNodeData";
            const string nodes = "World.Terrain.OctreeNodeResources";
            const string voxel = "World.Terrain.VoxelNodeData";
            const string transform = "Manager.SkyTransformData";
            const string loop = "Manager.GameloopManager";
            const string boolean = "System.Boolean";
            const string integer = "System.Int32";
            const string vector = "UnityEngine.Vector3";
            const string position = "Vector3l";
            try
            {
                var pulse = EvaluateOreDiscovery(module);
                if (!pulse.Available) throw new InvalidOperationException(pulse.Reason);
                var placement = EvaluateTerrainRadialPlacement(module);
                if (!placement.Available) throw new InvalidOperationException(placement.Reason);
                var frame = Method(module, loop, "OnAfterRendering", true, "System.Void");
                var frameState = module.GetType(loop)?.Fields.SingleOrDefault(field => field.Name == "jobFrameState");
                var operations = frame?.Body?.Instructions;
                if (operations == null || frameState == null
                    || !operations.Any(instruction => instruction.OpCode.Code == Code.Call
                        && instruction.Operand is MethodReference method && method.DeclaringType.FullName == loop
                        && method.Name == "CompleteJobs")
                    || !operations.Any(instruction => instruction.OpCode.Code == Code.Stsfld
                        && instruction.Operand is FieldReference field && field.FullName == frameState.FullName
                        && instruction.Previous?.OpCode.Code == Code.Ldc_I4_0
                        && instruction.Next?.OpCode.Code == Code.Ret)
                    || Method(module, loop, "get_IsNativeContainerAccessUnsafe", true, boolean)?.IsPublic != true)
                    throw new InvalidOperationException("Native completed-frame terrain read boundary changed.");

                if (!Field(module, nodes, "OctreeNodeDatas", "Common.NativeRecyclableContainer`1<" + node + ">", true)
                    || !Field(module, nodes, "voxelNodeDatas", "Common.SkyNativeChunkedList`1<" + voxel + ">", true)
                    || !Field(module, nodes, "saveParsed", boolean, true)
                    || !Field(module, node, "densityComputed", boolean, false)
                    || !Field(module, node, "isJobStarted", boolean, false)
                    || !Field(module, node, "densitiesId", integer, false)
                    || !Field(module, node, "treeGuid", "System.UInt64", false)
                    || !Field(module, node, "transformId", integer, false)
                    || !Field(module, node, "planetGenerationId", integer, false)
                    || !Field(module, node, "asteroidId", integer, false)
                    || !Field(module, node, "geoState", "World.Terrain.NodeGeoState", false)
                    || !Field(module, node, "fadeState", "World.Terrain.NodeFadeState", false)
                    || Method(module, node, "get_IsLeaf", false, boolean)?.IsPublic != true
                    || Method(module, node, "get_DensityBoundsTight", false, "UnityEngine.Bounds")?.IsPublic != true
                    || Method(module, voxel, "ToDensityId", true, integer, integer, integer, integer)?.IsPublic != true
                    || !Field(module, voxel, "density", "System.Int16", false)
                    || !Field(module, voxel, "texId0", "System.Byte", false)
                    || !Field(module, voxel, "texId1", "System.Byte", false))
                    throw new InvalidOperationException("Native resident voxel layout changed.");

                if (!ScannerConstant(module, node, "DENSITY_WIDTH", 20)
                    || !ScannerConstant(module, node, "DENSITY_HW", 10)
                    || !ScannerConstant(module, node, "DENSITY_COUNT", 8000)
                    || !ScannerConstant(module, node, "VOXEL_WIDTH", 16)
                    || !ScannerConstant(module, node, "VOXEL_HW", 8)
                    || !ScannerConstant(module, "World.Terrain.NodeFadeState", "fadeOut", 3)
                    || !ScannerConstant(module, "World.Terrain.NodeGeoState", "hasGeo", (int)World.Terrain.NodeGeoState.hasGeo))
                    throw new InvalidOperationException("Native resident density lattice or state changed.");

                if (!Field(module, transform, "datas", "Manager.SkyTransformDataContainer", true)
                    || !Field(module, transform, "scale", "System.Single", false)
                    || Method(module, transform, "LocalToSystem", false, position, vector)?.IsPublic != true
                    || Method(module, transform, "SystemToLocal", false, vector, position)?.IsPublic != true
                    || Method(module, "PositionManager", "ToWorldPosition", true, vector, position)?.IsPublic != true
                    || Method(module, scanner, "get_WidgetFadeAlpha", true, "System.Single")?.IsPublic != true)
                    throw new InvalidOperationException("Scanner coordinate or stock widget lifetime contract changed.");

                ValidateScannerAliases(module, node, voxel, transform, scanner);
                return new CapabilityResult(capability, true,
                    "Accepted stock pulse, completed native frame, resident leaf density and projected label lifetime validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static void ValidateScannerAliases(ModuleDefinition module, string node, string voxel, string transform, string scanner)
        {
            const string jobs = "World.Terrain.OctreeNodeJobs";
            const string job = jobs + "/Job_GeoCompute";
            const string record = "World.Terrain.OreScannerData";
            const string records = "Unity.Collections.NativeList`1<" + record + ">";
            const string voxels = "Unity.Collections.NativeArray`1<World.Terrain.VoxelNodeData>";
            var geometry = Method(module, job, "ComputeGeoState", false, "System.Void")?.Body?.Instructions;
            var update = Method(module, jobs, "UpdatePreviousJobs", true, "System.Void")?.Body?.Instructions;
            var publish = Method(module, jobs, "OnComputeGeoJobFinished", true, "System.Void", job)?.Body?.Instructions;
            var setTransform = Method(module, node, "SetTransform", false, "System.Void", "System.Int32")?.Body?.Instructions;

            if (geometry == null || update == null || publish == null || setTransform == null
                || !Field(module, job, "oreScannerDatas", records, false)
                || !Field(module, job, "voxelDatas", voxels, false)
                || !Field(module, job, "node", node, false)
                || !Field(module, record, "coordinate", "Vector3i", false)
                || !Field(module, record, "transformId", "System.Int32", false)
                || !Field(module, record, "texId", "System.Int32", false)
                || !Field(module, "World.TerrainMaterialData", "gradientId", "World.Terrain.MaterialGradientType", false)
                || Method(module, "World.Terrain.MaterialGradientGenerator", "GetMetalTextTexId", true,
                    "System.Int32", "World.Terrain.MaterialGradientType")?.IsPublic != true
                || Method(module, voxel, "get_MainTexId", false, "System.Int32")?.IsPublic != true
                || !geometry.Any(instruction => TerrainPlacementCall(instruction, voxel, "get_MainTexId"))
                || !geometry.Any(instruction => TerrainPlacementCall(instruction, node, "GetPosition"))
                || !geometry.Any(instruction => TerrainProviderField(instruction, Code.Stfld, record, "coordinate"))
                || !geometry.Any(instruction => TerrainProviderField(instruction, Code.Stfld, record, "localPosition"))
                || !geometry.Any(instruction => TerrainProviderField(instruction, Code.Stfld, record, "transformId"))
                || !setTransform.Any(instruction => TerrainProviderField(instruction, Code.Ldfld, node, "offsetToParent"))
                || !setTransform.Any(instruction => TerrainProviderField(instruction, Code.Stfld, transform, "localPosition"))
                || !setTransform.Any(instruction => TerrainProviderField(instruction, Code.Stfld, node, "transformId"))
                || !publish.Any(instruction => TerrainPlacementCall(instruction, scanner, "RegisterData")))
                throw new InvalidOperationException("Native ore record ownership or node-local coordinate contract changed.");

            // Stock records must carry the exact xyz locals that indexed the job's density buffer.
            var densityIndex = geometry.SingleOrDefault(instruction => instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == voxel && method.Name == "ToDensityId"
                && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "Unity.Mathematics.int3");
            var storedCoordinate = geometry.SingleOrDefault(instruction =>
                TerrainProviderField(instruction, Code.Stfld, record, "coordinate"));
            var densityCoordinates = densityIndex?.Previous;
            var recordCoordinates = storedCoordinate?.Previous;
            if (densityCoordinates?.OpCode.Code != Code.Newobj || recordCoordinates?.OpCode.Code != Code.Newobj
                || !SameCoordinateLocals(densityCoordinates, recordCoordinates))
                throw new InvalidOperationException("Stock ore record coordinates no longer identify the source voxel.");
            var completed = update.SingleOrDefault(instruction => TerrainPlacementCall(instruction, "Unity.Jobs.JobHandle", "Complete"));
            var callback = update.SingleOrDefault(instruction => TerrainPlacementCall(instruction, jobs, "OnComputeGeoJobFinished"));
            if (completed == null || callback == null || update.IndexOf(completed) >= update.IndexOf(callback))
                throw new InvalidOperationException("Native ore records are no longer published after geometry completion.");
        }
        private static bool SameCoordinateLocals(Instruction first, Instruction second)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                first = first.Previous;
                second = second.Previous;
                if (first == null || second == null || first.OpCode.Code is not (Code.Ldloc or Code.Ldloc_S)
                    || first.OpCode.Code != second.OpCode.Code
                    || first.Operand is not VariableDefinition left || second.Operand is not VariableDefinition right
                    || left.Index != right.Index)
                    return false;
            }
            return true;
        }
        private static bool ScannerConstant(ModuleDefinition module, string type, string name, int value)
            => module.GetType(type)?.Fields.Any(field => field.Name == name && field.IsLiteral
                && Convert.ToInt32(field.Constant) == value) == true;
    }
}
