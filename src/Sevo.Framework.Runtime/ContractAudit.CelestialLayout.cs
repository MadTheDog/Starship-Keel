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
            => EvaluateCelestialLayout(module, out _);

        internal static CapabilityResult EvaluateCelestialLayout(ModuleDefinition module, out bool correctPoiOrdinal)
        {
            const string capability = "celestialLayout";
            const string generator = "World.SolarBodyGenerator";
            const string body = "World.AstronData";
            const string solarBody = "World.SolarBody";
            const string star = "World.StarData";
            const string list = "System.Collections.Generic.List`1<" + body + ">";
            correctPoiOrdinal = false;
            try
            {
                var source = Method(module, generator, "GetStarSystemBodiesData", true, list, star);
                var stockGeneration = Method(module, generator, "GetCelestialBodyData", true, "System.Void",
                    list, star, "System.UInt64", "System.Int32", body, "World.OrbitalBodyType");
                if (source?.IsPublic != true || !source.HasBody
                    || !source.Body.Instructions.Any(instruction => IsLayoutCall(instruction, generator, "GetCelestialBodyData"))
                    || !HasLayoutCapacity(stockGeneration, generator, list)
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
                    || !HasLayoutInstantiation(Method(module, generator, "GetPlanetarySystem", true, solarBody, star, list), body, solarBody)
                    || !HasLayoutConsumer(module, "Game.MissionToolbox", generator)
                    || !HasLayoutConsumer(module, "Game.MissionWidgetTools", generator))
                    throw new InvalidOperationException("The system and mission paths no longer share the audited body catalog.");

                correctPoiOrdinal = ReadLayoutPoiOrdinalContract(module, list, body, star);
                return new CapabilityResult(capability, true, "Shared stock system and mission body catalog validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool HasLayoutCapacity(MethodDefinition? generation, string generator, string list)
            => generation?.HasBody == true && generation.Body.Instructions.Any(call =>
                IsLayoutCall(call, list, "get_Count") && call.Next != null
                && call.Next.Next is { } branch && branch.OpCode.Code is Code.Bge or Code.Bge_S
                && branch.Operand is Instruction target && target.Offset > branch.Offset
                && Constant(call.Next) >= CelestialLayoutProvider.BodyLimit
                && generation.Body.Instructions.Any(recurse => recurse.Offset > branch.Offset && recurse.Offset < target.Offset
                    && IsLayoutCall(recurse, generator, generation.Name)));

        private static bool HasLayoutInstantiation(MethodDefinition? method, string body, string solarBody)
        {
            if (method?.HasBody != true) return false;
            string bodies = "System.Collections.Generic.List`1<" + solarBody + ">";
            string enumerator = "System.Collections.Generic.List`1/Enumerator<" + body + ">";
            var code = method.Body.Instructions;
            var current = code.SingleOrDefault(instruction => IsLayoutCall(instruction, enumerator, "get_Current"));
            var create = code.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Newobj
                && instruction.Operand is MethodReference call && call.DeclaringType.FullName == solarBody
                && call.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(new[] { body, "World.StarData" }));
            var add = code.SingleOrDefault(instruction => IsLayoutCall(instruction, bodies, "Add"));
            var move = code.SingleOrDefault(instruction => IsLayoutCall(instruction, enumerator, "MoveNext"));
            return current != null && create != null && add != null && move != null
                && current.Offset < create.Offset && create.Offset < add.Offset && add.Offset < move.Offset
                && code.Count(instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch) == 1
                && move.Next.OpCode.Code is Code.Brtrue or Code.Brtrue_S
                && move.Next.Operand is Instruction repeat && repeat.Offset <= current.Offset;
        }

        private static bool ReadLayoutPoiOrdinalContract(ModuleDefinition module, string list, string body, string star)
        {
            const string station = "Game.StationWorldGeneration";
            const string poi = "World.BrickEntityPOIData";
            const string poiList = "System.Collections.Generic.List`1<" + poi + ">";
            var getPois = Method(module, station, "GetStarSystemPOIs", true, poiList, star, list);
            var add = Method(module, station, "AddPOIData", true, "System.Void", poiList, "Common.RNG.LongHash&", list, star);
            if (getPois?.HasBody != true || add?.HasBody != true
                || !getPois.Body.Instructions.Any(instruction => IsLayoutCall(instruction, station, add.Name))
                || module.GetType(poi)?.IsValueType != true || !Field(module, poi, "planetId", "System.Int32", false))
                throw new InvalidOperationException("The native layout POI writer changed.");

            var code = add.Body.Instructions;
            var store = code.SingleOrDefault(instruction => LayoutField(instruction, Code.Stfld, poi, "planetId"));
            var filter = code.SingleOrDefault(instruction => IsLayoutCall(instruction, list, "FindAll"));
            var select = code.SingleOrDefault(instruction => IsLayoutCall(instruction, list, "get_Item"));
            var predicate = code.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Ldftn)?.Operand as MethodReference;
            var predicateCode = predicate?.Resolve()?.Body?.Instructions;
            if (store?.Previous == null || filter?.Next == null || select?.Next == null
                || predicateCode == null || predicateCode.Count != 5
                || !LayoutField(predicateCode[1], Code.Ldfld, body, "orbitalBodyType")
                || Constant(predicateCode[2]) != 1 || predicateCode[3].OpCode.Code != Code.Ceq
                || LayoutLocal(filter.Next, store: true) is not int filteredList
                || LayoutLocal(select.Previous?.Previous) != filteredList
                || LayoutLocal(select.Previous) is not int selectedOrdinal
                || LayoutLocal(select.Next, store: true) is not int selectedBody
                || code.Count(instruction => IsLayoutCall(instruction, poiList, "Add")) != 1)
                throw new InvalidOperationException("The native deep-space POI no longer selects from the audited planet list.");

            if (LayoutLocal(store.Previous) == selectedOrdinal) return true;
            if (LayoutField(store.Previous, Code.Ldfld, body, "id")
                && LayoutLocal(store.Previous.Previous) == selectedBody) return false;
            throw new InvalidOperationException("The native deep-space POI body ID source changed.");
        }

        private static bool LayoutField(Instruction instruction, Code opcode, string type, string name)
            => instruction.OpCode.Code == opcode && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == type && field.Name == name;

        private static int? LayoutLocal(Instruction? instruction, bool store = false)
            => instruction?.OpCode.Code switch
            {
                Code.Stloc_0 when store => 0, Code.Stloc_1 when store => 1,
                Code.Stloc_2 when store => 2, Code.Stloc_3 when store => 3,
                Code.Ldloc_0 when !store => 0, Code.Ldloc_1 when !store => 1,
                Code.Ldloc_2 when !store => 2, Code.Ldloc_3 when !store => 3,
                Code.Stloc or Code.Stloc_S when store => (instruction.Operand as VariableDefinition)?.Index,
                Code.Ldloc or Code.Ldloc_S or Code.Ldloca or Code.Ldloca_S when !store => (instruction.Operand as VariableDefinition)?.Index,
                _ => null
            };

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
