using System.Linq;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateOrbitalPoints(ModuleDefinition module)
        {
            const string name = "orbitalPoints";
            const string generator = "Game.StationWorldGeneration";
            const string star = "World.StarData";
            const string point = "World.BrickEntityPOIData";
            const string body = "World.AstronData";
            const string pointList = "System.Collections.Generic.List`1<World.BrickEntityPOIData>";
            var catalog = Method(module, generator, "GetStarSystemPOIs", true, pointList, star,
                "System.Collections.Generic.List`1<World.AstronData>");
            var spawn = Method(module, "Game.SpaceStation", "SpawnSpaceStationOnPlanet", true, "System.Void",
                "Common.RNG.LongHash&", point, star, "System.Int32");
            bool available = catalog?.HasBody == true && spawn?.HasBody == true
                && !spawn.Body.HasExceptionHandlers
                && HasOneStationCall(spawn, "VoxelEngine.EntityMetaDataRepo", "GetMetaDataPath")
                && Method(module, "VoxelEngine.EntityMetaDataRepo", "GetMetaDataPath", true, "VoxelEngine.MetaDataPath",
                    "VoxelEngine.EntityMetaData", "System.Boolean")?.IsPublic == true
                && Field(module, point, "sguid", "System.String", false)
                && Field(module, point, "name", "System.String", false)
                && Field(module, point, "stationType", "Game.StationType", false)
                && Field(module, point, "metaData", "VoxelEngine.EntityMetaData", false)
                && Field(module, point, "isStartingStation", "System.Boolean", false)
                && Field(module, point, "systemPosition", "Vector3l", false)
                && Field(module, point, "planetId", "System.Int32", false)
                && Field(module, body, "systemPosition", "Vector3l", false)
                && Field(module, body, "seed", "System.UInt64", false)
                && Field(module, body, "id", "System.Int32", false)
                && Field(module, body, "radius", "System.Single", false)
                && spawn.Body.Instructions.Any(instruction => instruction.Operand is MethodReference reference
                    && reference.DeclaringType.FullName == "Player_NetworkSetup" && reference.Name == "ServerLoadBlueprint")
                && spawn.Body.Instructions.Any(instruction => instruction.Operand is FieldReference reference
                    && reference.DeclaringType.FullName == point && reference.Name == "sguid");
            return new CapabilityResult(name, available, available
                ? "Native POI catalog, coordinates and blueprint spawning validated."
                : "Native POI catalog, coordinates or blueprint spawning changed.");
        }
    }
}
