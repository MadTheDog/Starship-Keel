using System.Linq;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateStationServices(ModuleDefinition module)
        {
            const string name = "stationServices";
            const string page = "Game.UI_StationLandingPage";
            const string entity = "VoxelEngine.ParentEntity";
            const string row = "UI.GenericActionButton";
            const string button = "UI.GenericButtonData";
            var open = Method(module, page, "OpenHubUI", true, "System.Void", entity, entity, "System.Boolean");
            bool available = HasOneStationCall(open, "UI.UI_KioskGeneric", "ClearUI")
                && Method(module, page, "CloseUI", true, "System.Void")?.HasBody == true
                && Field(module, page, "station", entity, true) && Field(module, page, "UI_ActionButtons", row, true)
                && Field(module, row, "buttonData3", button, false) && Field(module, row, "buttonData4", button, false)
                && Field(module, row, "skip", "System.Boolean", false)
                && Field(module, button, "onButtonPressed", "System.Action", false)
                && Field(module, button, "label", "System.String", false);
            return new CapabilityResult(name, available, available
                ? "Native station action slots and window lifecycle validated."
                : "Native station action slots or window lifecycle changed.");
        }

        public static CapabilityResult EvaluateStationTraffic(ModuleDefinition module)
        {
            const string name = "stationTraffic";
            const string police = "Game.NpcSpacePolice";
            var spawn = Method(module, police, "OnNewStationGenerated", true, "System.Void",
                "VoxelEngine.ParentEntity", "Game.StationGenerator");
            bool available = HasOneStationCall(spawn, "Game.NPCSpawnCommon", "GetBlueprints")
                && HasOneStationCall(spawn, police, "DoSpawnPatrol")
                && HasOneStationCall(spawn, "Player_NetworkSetup", "ServerLoadBlueprint")
                && !spawn!.Body.HasExceptionHandlers
                && Method(module, "Game.NPCSpawnCommon", "GetBlueprints", true,
                    "System.Collections.Generic.List`1<VoxelEngine.MetaDataPath>",
                    "System.String", "System.String", "System.Boolean")?.IsPublic == true;
            return new CapabilityResult(name, available, available
                ? "Native patrol eligibility, blueprint selection and spawn validated."
                : "Native patrol eligibility, blueprint selection or spawn changed.");
        }

        private static bool HasOneStationCall(MethodDefinition? method, string type, string member)
            => method?.HasBody == true && method.Body.Instructions.Count(instruction =>
                instruction.Operand is MethodReference reference && reference.DeclaringType.FullName == type && reference.Name == member) == 1;
    }
}
