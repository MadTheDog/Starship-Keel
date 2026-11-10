using System.Linq;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateModFactions(ModuleDefinition module)
        {
            const string name = "modFactions";
            const string territory = "Game.FactionTerritoryMap";
            const string faction = "Game.NpcFactionData";
            var load = Method(module, territory, "LoadFromDisk", true, "System.Void", "System.String", "System.UInt64");
            bool available = module.GetType(territory)?.Fields.Count(field => field.IsStatic && field.Name == "factionsFromMods"
                && field.FieldType.FullName == "System.Collections.Generic.List`1<" + faction + ">") == 1
                && Method(module, faction, "get_FullSGUID", false, "System.String")?.IsPublic == true
                && Method(module, territory, "TryGetFactionBySGUID", true, "System.Boolean", "System.String", faction + "&")?.IsPublic == true
                && Method(module, "Game.PlayerProgression", "AddPoints", true, "System.Void",
                    "Game.ReputationType", "System.Int32", "System.Int32", "System.String")?.IsPublic == true
                && load?.HasBody == true
                && load.Body.Instructions.Any(instruction => instruction.Operand is FieldReference reference
                    && reference.DeclaringType.FullName == territory && reference.Name == "factionsFromMods")
                && Calls("TryLoadSaveFromDisk") && Calls("SeedAndDiffuseFactionTerritories");
            return new CapabilityResult(name, available, available
                ? "Native faction definitions, saved territory lookup and reputation paths validated."
                : "Native faction definition, territory or reputation path changed.");

            bool Calls(string member) => load?.HasBody == true && load.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference reference && reference.DeclaringType.FullName == territory && reference.Name == member);
        }
    }
}
