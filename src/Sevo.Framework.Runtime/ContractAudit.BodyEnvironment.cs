using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateBodyEnvironment(ModuleDefinition module)
        {
            const string capability = "bodyEnvironment";
            const string body = "World.SolarBody";
            const string generation = "World.Terrain.PlanetGenerationLogic";
            const string data = "World.Terrain.PlanetDensityGenerationData";
            const string palette = "World.Terrain.PlanetColorLogic/PlanetColorPalette";
            const string gravity = "World.Terrain.Planet.PlanetGravityData";
            const string planetResources = "World.Terrain.Planet.PlanetResources";
            const string gravityRows = "Common.NativeRecyclableContainer`1<" + gravity + ">";
            const string session = "PlayerState/SessionMode";
            const string number = "System.Single";
            try
            {
                foreach (var name in new[] { "waterAmount", "atmosphereDensity", "oxygenDensity", "oceanRadius" })
                    if (!Field(module, data, name, number, false))
                        throw new InvalidOperationException("Native environment field changed: " + name);
                foreach (var name in new[] { "atmosphereColor", "oceanColorMain", "oceanColorSec", "oceanColorDepth" })
                    if (!Field(module, palette, name, "UnityEngine.Color32", false))
                        throw new InvalidOperationException("Native environment palette field changed: " + name);
                if (!Field(module, data, "planetColorPalette", palette, false)
                    || !Field(module, data, "atmosphereColor", "Unity.Mathematics.float3", false)
                    || !Field(module, body, "parentStar", "World.StarData", false)
                    || !Field(module, body, "planetGenerationId", "System.Int32", false)
                    || !Field(module, body, "planetGenerationData", data, false)
                    || !Field(module, "World.StarData", "seed", "System.UInt64", false)
                    || Method(module, body, "get_Seed", false, "System.UInt64")?.IsPublic != true
                    || Method(module, body, "get_AstronType", false, "World.AstronType")?.IsPublic != true
                    || Method(module, "PlayerState", "get_sessionMode", true, session)?.IsPublic != true
                    || !Equals(module.GetType(session)?.Fields.SingleOrDefault(value => value.Name == "solo" && value.IsLiteral)?.Constant, 1)
                    || !Equals(module.GetType("World.AstronType")?.Fields.SingleOrDefault(value => value.Name == "telluric" && value.IsLiteral)?.Constant, 1))
                    throw new InvalidOperationException("Native body environment identity or solo-session contract changed.");

                var generator = Method(module, generation, "GetPlanetData", true, data, body, "System.Int32", "System.Boolean");
                var physics = Method(module, body, "SetPhysicsData", false, "System.Void");
                var setup = Method(module, body, "SetupInstanceObject", false, "System.Void");
                var initialize = Method(module, body, "InitializePlanetGenerationData", false, "System.Void");
                if (!ManagedBody(generator) || !ManagedBody(physics) || setup?.HasBody != true || initialize?.HasBody != true)
                    throw new InvalidOperationException("Managed body environment generation or publication seam changed.");

                var instructions = generator!.Body.Instructions;
                int water = Stores(generator, data, "waterAmount", 1).Single();
                int atmosphere = Stores(generator, data, "atmosphereDensity", 1).Single();
                int ocean = Stores(generator, data, "oceanRadius", 1).Single();
                int[] oxygen = Stores(generator, data, "oxygenDensity", 2);
                int colors = Stores(generator, data, "planetColorPalette", 1).Single();
                int atmosphereColor = Stores(generator, data, "atmosphereColor", 1).Single();
                var biome = Method(module, generation, "GetBiomeData", true, "World.Terrain.BiomeData",
                    "System.UInt64", "System.Int32", data, "Manager.StartingLocationType");
                var biomes = instructions.Where(instruction => CallsBodyMethod(instruction, generation, "GetBiomeData", 4)
                    && ((MethodReference)instruction.Operand).FullName == biome?.FullName)
                    .Select(instructions.IndexOf).ToArray();
                if (biomes.Length != 2 || !(water < atmosphere && atmosphere < ocean && ocean < oxygen[0]
                    && oxygen[0] < colors && colors < atmosphereColor && atmosphereColor < biomes[0] && biomes[1] < oxygen[1])
                    || !instructions.Skip(water + 1).Take(atmosphere - water - 1).Any(instruction => Reads(instruction, data, "waterAmount"))
                    || !instructions.Skip(colors + 1).Take(atmosphereColor - colors - 1).Any(instruction => Reads(instruction, data, "planetColorPalette")))
                    throw new InvalidOperationException("Native environment settings no longer precede their dependent calculations.");

                var generationCall = setup.Body.Instructions.SingleOrDefault(instruction =>
                    CallsBodyMethod(instruction, generation, "GetPlanetData", 3)
                    && ((MethodReference)instruction.Operand).FullName == generator.FullName);
                if (generationCall == null || setup.Body.Instructions.IndexOf(generationCall) >= UniqueCall(setup, body, "InitializePlanetGenerationData")
                    || !initialize.Body.Instructions.Any(instruction => Reads(instruction, data, "oceanRadius"))
                    || !initialize.Body.Instructions.Any(instruction => CallsBodyMethod(instruction, "World.Terrain.OceanNodeData", "AllocateParentNodes", 4))
                    || !initialize.Body.Instructions.Any(instruction => IsCall(instruction, body, "SetPhysicsData")))
                    throw new InvalidOperationException("Native environment payload no longer feeds ocean and physics initialization.");

                foreach (var name in new[] { "g", "oxygenDensity", "atmosphereDensity", "oceanRadius" })
                    if (!Field(module, gravity, name, number, false))
                        throw new InvalidOperationException("Native gravity row field changed: " + name);
                int strength = Stores(physics!, gravity, "g", 1).Single();
                var publishCall = physics!.Body.Instructions.SingleOrDefault(instruction =>
                    CallsBodyMethod(instruction, gravityRows, "set_Item", 2));
                int publish = publishCall == null ? -1 : physics.Body.Instructions.IndexOf(publishCall);
                if (!Field(module, planetResources, "PlanetGravityDatas", gravityRows, true)
                    || strength >= publish
                    || !physics!.Body.Instructions.Take(strength).Any(instruction =>
                        instruction.OpCode.Code == Code.Ldsflda && instruction.Operand is FieldReference field
                        && field.DeclaringType.FullName == planetResources && field.Name == "PlanetGravityDatas")
                    || new[] { "oxygenDensity", "atmosphereDensity", "oceanRadius" }.Any(name =>
                        Stores(physics, gravity, name, 1).Single() >= publish
                        || !physics.Body.Instructions.Any(instruction => Reads(instruction, data, name))))
                    throw new InvalidOperationException("Native environment fields no longer feed the gravity row.");

                return new CapabilityResult(capability, true, "Native rocky-body environment generation and gravity publication validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool ManagedBody(MethodDefinition? method) => method?.HasBody == true
            && !method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute");

        private static bool CallsBodyMethod(Instruction instruction, string type, string name, int parameterCount)
            => (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type
                && method.Name == name && method.Parameters.Count == parameterCount;

        private static int[] Stores(MethodDefinition method, string type, string name, int expectedCount)
        {
            var instructions = method.Body.Instructions;
            var stores = instructions.Where(instruction => instruction.OpCode.Code == Code.Stfld && RefersTo(instruction, type, name))
                .Select(instructions.IndexOf).ToArray();
            if (stores.Length != expectedCount)
                throw new InvalidOperationException("Native environment store count changed: " + type + "." + name);
            return stores;
        }

        private static bool Reads(Instruction instruction, string type, string name)
            => (instruction.OpCode.Code == Code.Ldfld || instruction.OpCode.Code == Code.Ldflda) && RefersTo(instruction, type, name);

        private static bool RefersTo(Instruction instruction, string type, string name)
            => instruction.Operand is FieldReference field && field.DeclaringType.FullName == type && field.Name == name;
    }
}
