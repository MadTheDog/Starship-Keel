using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string TerrainBody = "World.SolarBody";
        private const string TerrainPlanetResources = "World.Terrain.Planet.PlanetResources";
        private const string TerrainCompute = "World.Terrain.DensityComputeJob";
        private const string TerrainPlanetLogic = "World.Terrain.PlanetGenerationLogic";
        private const string TerrainAsteroidInstance = "World.Terrain.AsteroidData";
        private const string TerrainVoid = "System.Void";
        private static readonly string[] PlanetProviderFields = { "craterAmount", "continentThreshold", "canyonStrength" };
        private static readonly string[] AsteroidProviderFields = { "craterAmount", "craterScaleFactor" };

        public static CapabilityResult EvaluatePlanetTerrainGeneration(ModuleDefinition module)
        {
            const string capability = "planetTerrainGeneration";
            try
            {
                RequireTerrainProviderContext(module);
                RequireTerrainProviderFields(module, Planet, PlanetProviderFields.Concat(new[]
                {
                    "canyonFreq", "canyonWarp", "canyonSmoothOffset"
                }).ToArray());
                var source = RequireTerrainProviderMethod(module, TerrainPlanetLogic, "GetPlanetData", true,
                    Planet, TerrainBody, "System.Int32", "System.Boolean");
                RequireTerrainSourceFields(source, Planet, PlanetProviderFields);
                var setup = RequireTerrainProviderMethod(module, TerrainBody, "SetupInstanceObject", false, TerrainVoid);
                var initialize = RequireTerrainProviderMethod(module, TerrainBody, "InitializePlanetGenerationData", false, TerrainVoid);
                var sourceCall = TerrainProviderCall(setup, source);
                var initialization = TerrainProviderCall(setup, initialize);
                if (sourceCall < 0 || initialization <= sourceCall
                    || !setup.Body.Instructions.Skip(sourceCall + 1).Take(initialization - sourceCall - 1)
                        .Any(instruction => TerrainProviderField(instruction, Code.Stfld, TerrainBody, "planetGenerationData"))
                    || !initialize.Body.Instructions.Any(instruction => TerrainProviderField(instruction, Code.Ldsflda,
                        TerrainPlanetResources, "Datas")))
                    throw new InvalidOperationException("Native planet terrain publication changed.");

                var regenerate = RequireTerrainProviderMethod(module, TerrainBody, "RegenerateRandom", false, TerrainVoid, "System.UInt64");
                var regeneratePlanet = RequireTerrainProviderMethod(module, TerrainBody, "RegeneratePlanet", false, TerrainVoid, Planet);
                if (TerrainProviderCall(regenerate, source) < 0 || TerrainProviderCall(regenerate, regeneratePlanet) < 0
                    || TerrainProviderCall(regeneratePlanet, initialize) < 0)
                    throw new InvalidOperationException("Native planet regeneration path changed.");

                var normalize = RequireTerrainProviderMethod(module, Planet, "Initialize", false, TerrainVoid, "System.Single");
                RequireTerrainFieldsPreserved(normalize, Planet, PlanetProviderFields);
                var dispatch = RequireTerrainProviderMethod(module, TerrainCompute, "Dispatch", false, TerrainVoid,
                    "System.Int32", TerrainCompute + "/JobNodeData");
                if (TerrainProviderCall(dispatch, normalize) < 0 || !dispatch.Body.Instructions.Any(instruction =>
                    TerrainProviderField(instruction, Code.Ldsflda, TerrainPlanetResources, "Datas")))
                    throw new InvalidOperationException("Native planet terrain compute source changed.");
                RequireTerrainUpload(dispatch, "PlanetDatas", "planetDataBuffer", "_PlanetDataBuffer");
                return new CapabilityResult(capability, true, "Planet terrain generation, native publication and density upload validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateAsteroidTerrainGeneration(ModuleDefinition module)
        {
            const string capability = "asteroidTerrainGeneration";
            try
            {
                RequireTerrainProviderContext(module);
                RequireTerrainProviderMethod(module, TerrainBody, "get_planetId", false, "System.Int32");
                RequireTerrainProviderFields(module, Asteroid, AsteroidProviderFields);
                var source = RequireTerrainProviderMethod(module, Asteroid, "GetAsteroidDataTemplate", true, Asteroid, TerrainBody);
                RequireTerrainSourceFields(source, Asteroid, AsteroidProviderFields);
                var rings = RequireTerrainProviderMethod(module, TerrainBody, "SpawnRings", false, TerrainVoid);
                int sourceCall = TerrainProviderCall(rings, source);
                if (sourceCall < 0 || !rings.Body.Instructions.Skip(sourceCall + 1).Any(instruction =>
                    TerrainProviderField(instruction, Code.Stfld, TerrainBody, "asteroidTemplateData"))
                    || !rings.Body.Instructions.Skip(sourceCall + 1).Any(instruction =>
                        TerrainProviderField(instruction, Code.Ldsflda, TerrainPlanetResources, "AsteroidGenDatas")))
                    throw new InvalidOperationException("Native ring asteroid template publication changed.");

                var create = RequireTerrainProviderMethod(module, Asteroid, "CreateInstance", false, TerrainVoid, "System.Single", "System.UInt32");
                var normalize = RequireTerrainProviderMethod(module, Asteroid, "Initialize", false, TerrainVoid, "System.Single");
                RequireTerrainFieldsPreserved(create, Asteroid, AsteroidProviderFields);
                RequireTerrainFieldsPreserved(normalize, Asteroid, AsteroidProviderFields);
                var instance = RequireTerrainProviderMethod(module, TerrainAsteroidInstance, "get_AsteroidDensityGenerationData", false, Asteroid);
                if (TerrainProviderCall(instance, create) < 0 || !instance.Body.Instructions.Any(instruction =>
                    TerrainProviderField(instruction, Code.Ldsflda, TerrainPlanetResources, "AsteroidGenDatas")))
                    throw new InvalidOperationException("Native asteroid instance template binding changed.");
                var dispatch = RequireTerrainProviderMethod(module, TerrainCompute, "Dispatch", false, TerrainVoid,
                    "System.Int32", TerrainCompute + "/JobNodeData");
                var immediate = RequireTerrainProviderMethod(module, TerrainCompute, "DispatchImmediate", false, TerrainVoid,
                    "World.Terrain.OctreeNodeData", TerrainAsteroidInstance, "World.Terrain.VoxelNodeData[]");
                foreach (var consumer in new[] { dispatch, immediate })
                {
                    if (TerrainProviderCall(consumer, instance) < 0 || TerrainProviderCall(consumer, normalize) < 0)
                        throw new InvalidOperationException("Native asteroid density source changed: " + consumer.Name);
                    RequireTerrainUpload(consumer, "AsteroidDatas", "asteroidDataBuffer", "_AsteroidDataBuffer");
                }
                return new CapabilityResult(capability, true, "Ring asteroid template, instance and density uploads validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static void RequireTerrainProviderContext(ModuleDefinition module)
        {
            RequireTerrainSoloContext(module);
            if (!RequireTerrainProviderMethod(module, TerrainBody, "get_Seed", false, "System.UInt64").IsPublic
                || !RequireTerrainProviderMethod(module, TerrainBody, "get_IsStartingPlanet", false, "System.Boolean").IsPublic)
                throw new InvalidOperationException("Terrain provider seed or starting location context changed.");
        }

        private static void RequireTerrainSoloContext(ModuleDefinition module)
        {
            const string session = "PlayerState/SessionMode";
            var solo = module.GetType(session)?.Fields.SingleOrDefault(field => field.IsLiteral && field.Name == "solo");
            if (!Equals(solo?.Constant, 1)
                || !RequireTerrainProviderMethod(module, "PlayerState", "get_sessionMode", true, session).IsPublic)
                throw new InvalidOperationException("Terrain solo session context changed.");
        }

        private static MethodDefinition RequireTerrainProviderMethod(ModuleDefinition module, string type, string name,
            bool isStatic, string result, params string[] arguments)
        {
            var method = Method(module, type, name, isStatic, result, arguments);
            if (method?.HasBody != true)
                throw new InvalidOperationException("Missing managed terrain provider dependency: " + type + "." + name);
            return method;
        }

        private static void RequireTerrainProviderFields(ModuleDefinition module, string type, string[] fields)
        {
            var row = module.GetType(type);
            if (row?.IsPublic != true || !row.IsValueType || !row.IsSequentialLayout
                || fields.Any(field => !Field(module, type, field, "System.Single", false)))
                throw new InvalidOperationException("Native terrain parameter layout changed: " + type);
        }

        private static void RequireTerrainSourceFields(MethodDefinition source, string type, string[] fields)
        {
            if (!source.IsPublic || source.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute")
                || fields.Any(field => !source.Body.Instructions.Any(instruction => TerrainProviderField(instruction, Code.Stfld, type, field))))
                throw new InvalidOperationException("Native terrain parameter construction changed: " + source.FullName);
        }

        private static void RequireTerrainFieldsPreserved(MethodDefinition method, string type, string[] fields)
        {
            if (fields.Any(field => method.Body.Instructions.Any(instruction => TerrainProviderField(instruction, Code.Stfld, type, field))))
                throw new InvalidOperationException("Native instance initialization now replaces terrain parameters: " + method.FullName);
        }

        private static void RequireTerrainUpload(MethodDefinition method, string data, string buffer, string property)
        {
            var body = method.Body.Instructions;
            var upload = body.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Callvirt
                && instruction.Operand is MethodReference call && call.DeclaringType.FullName == "UnityEngine.ComputeBuffer"
                && call.Name == "SetData" && instruction.Previous != null
                && TerrainProviderField(instruction.Previous, Code.Ldsfld, TerrainCompute, data)
                && instruction.Previous.Previous != null
                && TerrainProviderField(instruction.Previous.Previous, Code.Ldfld, TerrainCompute, buffer));
            var binding = body.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, property));
            if (upload == null || binding == null || body.IndexOf(upload) >= body.IndexOf(binding)
                || !body.Skip(body.IndexOf(binding) + 1).Take(5).Any(instruction => instruction.Operand is MethodReference call
                    && (call.Name == "SetComputeBufferParam" || call.Name == "SetBuffer")))
                throw new InvalidOperationException("Native terrain buffer upload or shader binding changed: " + property);
        }

        private static int TerrainProviderCall(MethodDefinition source, MethodDefinition target)
        {
            var body = source.Body.Instructions;
            var call = body.SingleOrDefault(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference reference && reference.FullName == target.FullName);
            return call == null ? -1 : body.IndexOf(call);
        }

        private static bool TerrainProviderField(Instruction instruction, Code opcode, string type, string name)
            => instruction.OpCode.Code == opcode && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == type && field.Name == name;
    }
}
