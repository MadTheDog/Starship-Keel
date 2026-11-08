using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        internal const int NativeOreSlotCount = 6;
        private const string Landform = "World.Terrain.LandformCommon";
        private const string OreData = Landform + "/Lf_OreData";
        private const string Planet = "World.Terrain.PlanetDensityGenerationData";
        private const string Asteroid = "World.Terrain.AsteroidDensityGenerationData";
        private const string Biome = "World.Terrain.BiomeData";
        private const string Scanner = "World.Terrain.OreScannerResources";
        private const string Widget = "World.Terrain.OreWidgetData";
        private const string WidgetList = "Unity.Collections.NativeList`1<" + Widget + ">";
        private const string MaterialResources = "World.WorldTerrainResources";
        private const string MaterialRow = "World.TerrainMaterialData";
        private const string MaterialArray = "Unity.Collections.NativeArray`1<" + MaterialRow + ">";

        /// <summary>Validates the managed seam and existing GPU payload fields without matching build identity.</summary>
        public static CapabilityResult EvaluateOreGeneration(ModuleDefinition module)
        {
            const string capability = "oreGeneration";
            try
            {
                var oreData = module.GetType(OreData) ?? throw new InvalidOperationException("Missing native ore data.");
                var count = oreData.Fields.SingleOrDefault(field => field.Name == "ORE_COUNT" && field.IsLiteral);
                var fields = oreData.Fields.Where(field => !field.IsStatic).ToArray();
                if (!oreData.IsNestedPublic || !oreData.IsValueType || !oreData.IsSequentialLayout
                    || !Equals(count?.Constant, NativeOreSlotCount) || fields.Length != 4
                    || fields[3].Name != "totalWeight" || fields[3].FieldType.FullName != "System.Single"
                    || !fields[3].IsPublic)
                    throw new InvalidOperationException("Native ore payload layout changed.");

                RequireFixedFloatField(fields[0], "oreSize", NativeOreSlotCount);
                RequireFixedFloatField(fields[1], "oreThreshold", NativeOreSlotCount);
                RequireFixedFloatField(fields[2], "oreOffsets", NativeOreSlotCount * 3);
                var source = Method(module, Landform, "GetOreData", true, OreData,
                    "Common.RNG.HashDrawSet", "System.UInt32", "Manager.StartingLocationType", "System.Boolean");
                if (source?.IsPublic != true || !source.HasBody
                    || source.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute")
                    || fields.Any(field => !source.Body.Instructions.Any(instruction =>
                        instruction.Operand is FieldReference reference && reference.FullName == field.FullName)))
                    throw new InvalidOperationException("Managed ore generation seam changed.");

                if (!Field(module, Biome, "lfOreData", OreData, false)
                    || !Field(module, Asteroid, "lfOreData", OreData, false)
                    || !Field(module, Planet, "biome0", Biome, false)
                    || !Field(module, Planet, "biome1", Biome, false))
                    throw new InvalidOperationException("Native planet or asteroid ore payload binding changed.");
                const string sessionType = "PlayerState/SessionMode";
                var solo = module.GetType(sessionType)?.Fields.SingleOrDefault(field => field.Name == "solo" && field.IsLiteral);
                if (!Equals(solo?.Constant, 1)
                    || Method(module, "PlayerState", "get_sessionMode", true, sessionType)?.IsPublic != true)
                    throw new InvalidOperationException("Native solo-session guard changed.");

                RequireTerrainConsumer(module.GetType("World.Terrain.PlanetGenerationLogic"), source, Biome, "lfOreData");
                RequireTerrainConsumer(module.GetType(Asteroid), source, Asteroid, "lfOreData");
                return new CapabilityResult(capability, true, "Managed generation and fixed planet/asteroid ore slots validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        /// <summary>Checks the native-to-managed publication point separately from terrain generation.</summary>
        public static CapabilityResult EvaluateOreScanner(ModuleDefinition module)
        {
            const string capability = "oreScannerSnapshots";
            try
            {
                if (!Field(module, Scanner, "WidgetResults", WidgetList, true)
                    || Method(module, Scanner, "get_IsScannerRunning", true, "System.Boolean")?.IsPublic != true)
                    throw new InvalidOperationException("Ore scanner snapshot source changed.");
                foreach (var position in new[] { "worldPos", "worldPosMin", "worldPosMax" })
                    if (!Field(module, Widget, position, "Unity.Mathematics.float3", false))
                        throw new InvalidOperationException("Ore scanner position contract changed: " + position);
                if (!Field(module, Widget, "color", "System.UInt32", false)
                    || !Field(module, Widget, "texId", "System.Int32", false)
                    || !Field(module, Widget, "count", "System.Int32", false))
                    throw new InvalidOperationException("Ore scanner widget fields changed.");

                var dispose = Method(module, Scanner, "DisposeWidgetJob", true, "System.Void");
                if (dispose?.IsPublic != true || !dispose.HasBody || !dispose.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "Manager.DisposeJobCallback"))
                    throw new InvalidOperationException("Managed ore scanner completion callback changed.");
                var instructions = dispose.Body.Instructions;
                var publication = instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference method
                    && method.DeclaringType.FullName == WidgetList && method.Name == "AddRange");
                if (publication == null || !instructions.TakeWhile(instruction => instruction != publication).Any(instruction =>
                    instruction.OpCode.Code == Code.Ldsflda && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == Scanner && field.Name == "WidgetResults"))
                    throw new InvalidOperationException("Ore scanner callback no longer publishes completed widget results.");
                return new CapabilityResult(capability, true, "Managed publication of completed native scanner results validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        /// <summary>Checks the stock visual row write that precedes native terrain buffer upload.</summary>
        public static CapabilityResult EvaluateTerrainMaterialVisuals(ModuleDefinition module)
        {
            const string capability = "terrainMaterialVisuals";
            try
            {
                const string source = MaterialResources + "/TerrainTexture";
                if (!Field(module, MaterialResources, "terrainTextures", "System.Collections.Generic.List`1<" + source + ">", false)
                    || !Field(module, source, "ShaderName", "System.String", false)
                    || !Field(module, MaterialResources, "TerrainMaterialDatas", MaterialArray, true))
                    throw new InvalidOperationException("Native terrain material identifiers changed.");
                foreach (var field in new[] { "textureScale", "brightnessFactor" })
                    if (!Field(module, MaterialRow, field, "System.Single", false))
                        throw new InvalidOperationException("Native terrain visual field changed: " + field);

                var push = Method(module, MaterialResources, "PushMaterialDatas", false, "System.Void");
                var load = Method(module, MaterialResources, "LoadResources", false, "System.Void", "System.Boolean");
                if (push?.HasBody != true || push.IsPublic || load?.HasBody != true
                    || !load.Body.Instructions.Any(instruction => IsCall(instruction, MaterialResources, "PushMaterialDatas")))
                    throw new InvalidOperationException("Managed terrain material loading changed.");
                var instructions = push.Body.Instructions;
                var setter = instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Call
                    && instruction.Operand is MethodReference method && method.DeclaringType.FullName == MaterialArray
                    && method.Name == "set_Item" && method.ReturnType.FullName == "System.Void"
                    && method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                        .SequenceEqual(new[] { "System.Int32", "!0" }));
                var upload = instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Callvirt
                    && instruction.Operand is MethodReference method && method.Name == "PushData"
                    && method.DeclaringType.FullName == "Common.ComputeBufferHandler`1<" + MaterialRow + ">"
                    && method.ReturnType.FullName == "System.Void" && method.Parameters.Count == 2
                    && method.Parameters[1].ParameterType.FullName == "System.Int32");
                if (setter == null || upload == null || setter.Offset >= upload.Offset
                    || !instructions.TakeWhile(instruction => instruction != setter).Any(instruction =>
                        instruction.OpCode.Code == Code.Ldsflda && instruction.Operand is FieldReference field
                        && field.DeclaringType.FullName == MaterialResources && field.Name == "TerrainMaterialDatas")
                    || !instructions.SkipWhile(instruction => instruction != setter).TakeWhile(instruction => instruction != upload)
                        .Any(instruction => instruction.OpCode.Code == Code.Ldsfld && instruction.Operand is FieldReference field
                            && field.DeclaringType.FullName == MaterialResources && field.Name == "TerrainMaterialDatas"))
                    throw new InvalidOperationException("Terrain material row-write or upload contract changed.");

                return new CapabilityResult(capability, true, "Stock terrain material visual row publication validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static void RequireFixedFloatField(FieldDefinition field, string name, int length)
        {
            var buffer = field.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.FixedBufferAttribute");
            var inline = field.FieldType.Resolve();
            if (field.Name != name || !field.IsPublic || buffer?.ConstructorArguments.Count != 2
                || !(buffer.ConstructorArguments[0].Value is TypeReference item) || item.FullName != "System.Single"
                || !Equals(buffer.ConstructorArguments[1].Value, length) || inline == null
                || !inline.IsValueType || !inline.IsSequentialLayout || inline.ClassSize != length * sizeof(float)
                || inline.Fields.Count != 1 || inline.Fields[0].FieldType.FullName != "System.Single")
                throw new InvalidOperationException("Native ore fixed-buffer contract changed: " + name);
        }

        private static void RequireTerrainConsumer(TypeDefinition? type, MethodDefinition source, string dataType, string fieldName)
        {
            if (type == null) throw new InvalidOperationException("Native ore generation consumer is missing: " + dataType);
            var consumers = type.Methods.Where(method => method.HasBody && method.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Call && instruction.Operand is MethodReference call
                && call.FullName == source.FullName)).ToArray();
            if (consumers.Length != 1 || !consumers[0].Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stfld
                && instruction.Operand is FieldReference field && field.DeclaringType.FullName == dataType
                && field.Name == fieldName && field.FieldType.FullName == OreData))
                throw new InvalidOperationException("Native ore generation consumer changed: " + type.FullName);
        }
    }
}
