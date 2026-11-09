using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the managed layout source and its world and mission consumers.</summary>
        public static CapabilityResult EvaluateCelestialLayout(ModuleDefinition module)
        {
            const string capability = "celestialLayout";
            const string generator = "World.SolarBodyGenerator";
            const string body = "World.AstronData";
            const string solarBody = "World.SolarBody";
            const string star = "World.StarData";
            const string list = "System.Collections.Generic.List`1<" + body + ">";
            try
            {
                var source = Method(module, generator, "GetStarSystemBodiesData", true, list, star);
                if (source?.IsPublic != true || !source.HasBody
                    || !source.Body.Instructions.Any(instruction => IsLayoutCall(instruction, generator, "GetCelestialBodyData"))
                    || !Field(module, star, "seed", "System.UInt64", false))
                    throw new InvalidOperationException("Stock celestial catalog generation changed.");

                var fields = new[]
                {
                    ("id", "System.Int32"), ("parentId", "System.Int32"), ("satelliteId", "System.Int32"),
                    ("satellitesCount", "System.Int32"), ("seed", "System.UInt64"), ("radius", "System.Single"),
                    ("hasRing", "System.Boolean"), ("ringOuterRadius", "System.Single"),
                    ("orbitalBodyType", "World.OrbitalBodyType"), ("celestialBodyType", "World.AstronType"),
                    ("name", "System.String"), ("systemPosition", "Vector3l"), ("orbitPosition", "Vector3l"),
                    ("orbitSemiRadius", "System.Single"), ("angularPosition", "System.Single"), ("ellipse", "Common.Ellipse")
                };
                if (module.GetType(body)?.IsValueType != true
                    || fields.Any(field => !Field(module, body, field.Item1, field.Item2, false))
                    || !HasLayoutEnum(module, "World.OrbitalBodyType", "star", 0)
                    || !HasLayoutEnum(module, "World.OrbitalBodyType", "planet", 1)
                    || !HasLayoutEnum(module, "World.OrbitalBodyType", "moon", 2)
                    || !HasLayoutEnum(module, "World.AstronType", "gas", 0)
                    || !HasLayoutEnum(module, "World.AstronType", "telluric", 1)
                    || !HasLayoutEnum(module, "PlayerState/SessionMode", "solo", 1))
                    throw new InvalidOperationException("Stock body data or mode contract changed.");

                if (Method(module, solarBody, "GenerateOrbitalParameters", true, "System.Void",
                        "Common.RNG.HashDrawSet", body, body + "&")?.IsPublic != true
                    || Method(module, solarBody, "CreateName", true, "System.String", body + "&", "System.String")?.IsPublic != true
                    || Method(module, solarBody, "SetHasPlanetaryRing", true, "System.Boolean",
                        "Common.RNG.HashDrawSet", "World.AstronType", "World.OrbitalBodyType", "System.Boolean")?.IsPublic != true
                    || Method(module, "Manager.GameIntroductionResources", "IsStartingSystem", true, "System.Boolean", star)?.IsPublic != true)
                    throw new InvalidOperationException("Stock orbit, naming or introduction contracts changed.");

                var states = module.GetType("World.PlanetarySystem")?.NestedTypes
                    .SelectMany(type => type.Methods).Where(method => method.Name == "MoveNext" && method.HasBody);
                if (states == null || !states.Any(method =>
                    HasOrderedLayoutCalls(method, generator, "GetStarSystemBodiesData", "GetStarSystemPOIs", "GetPlanetarySystem"))
                    || !HasLayoutConsumer(module, "Game.MissionToolbox", generator)
                    || !HasLayoutConsumer(module, "Game.MissionWidgetTools", generator))
                    throw new InvalidOperationException("The system and mission paths no longer share the audited body catalog.");

                return new CapabilityResult(capability, true, "Shared stock system and mission body catalog validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool IsLayoutCall(Instruction instruction, string type, string name)
            => (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == type && call.Name == name;

        private static bool HasLayoutEnum(ModuleDefinition module, string type, string name, int value)
            => Equals(module.GetType(type)?.Fields.SingleOrDefault(field => field.IsLiteral && field.Name == name)?.Constant, value);

        private static bool HasLayoutConsumer(ModuleDefinition module, string type, string generator)
            => module.GetType(type)?.Methods.Any(method => method.HasBody && method.Body.Instructions
                .Any(instruction => IsLayoutCall(instruction, generator, "GetStarSystemBodiesData"))) == true;

        private static bool HasOrderedLayoutCalls(MethodDefinition method, string generator,
            string source, string pointsOfInterest, string instantiate)
        {
            var calls = method.Body.Instructions;
            int from = calls.ToList().FindIndex(instruction => IsLayoutCall(instruction, generator, source));
            int spawn = calls.ToList().FindIndex(instruction => IsLayoutCall(instruction, generator, instantiate));
            return from >= 0 && spawn > from && calls.Skip(from + 1).Take(spawn - from - 1)
                .Any(instruction => IsLayoutCall(instruction, "Game.StationWorldGeneration", pointsOfInterest));
        }
    }
}
