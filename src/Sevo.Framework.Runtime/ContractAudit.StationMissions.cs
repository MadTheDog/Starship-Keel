using System.Linq;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateStationMissions(ModuleDefinition module)
        {
            const string name = "stationMissions";
            const string save = "Game.MissionSaveData";
            const string mission = "Game.MissionBase";
            const string station = "VoxelEngine.EntityStation";
            var spawn = Method(module, "World.WorldEntities", "SpawnMission", true, "System.Void", station);
            var load = Method(module, "Game.PlayerMissions", "LoadMissionSaveDatas", true, "System.Void",
                "System.Collections.Generic.List`1<Game.MissionSaveData>");
            var decode = Method(module, save, "Load", false, mission);
            bool available = Field(module, save, "TagByType", "System.Collections.Generic.Dictionary`2<System.Type,System.String>", true)
                && Field(module, station, "Missions", "System.Collections.Generic.List`1<Game.MissionBase>", false)
                && Field(module, mission, "rewards", "Game.MissionRewards", false)
                && Method(module, "World.BrickEntityPOIData", ".ctor", false, "System.Void", "VoxelEngine.ParentEntity")?.IsPublic == true
                && Calls(spawn, mission, "OnIsAddedToMissionBoard") && Calls(load, save, "Load")
                && Calls(decode, "System.Activator", "CreateInstance")
                && decode!.Body.Instructions.Any(instruction => instruction.Operand is FieldReference reference
                    && reference.DeclaringType.FullName == save && reference.Name == "TagByType");
            return new CapabilityResult(name, available, available
                ? "Native mission board, reward field and save-tag loader validated."
                : "Native mission board, rewards or save-tag loader changed.");

            static bool Calls(MethodDefinition? method, string type, string member)
                => method?.HasBody == true && method.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference reference && reference.DeclaringType.FullName == type && reference.Name == member);
        }
    }
}
