using System.Linq;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateStationStock(ModuleDefinition module)
        {
            const string name = "stationStock";
            const string page = "Game.UI_StationLandingPage";
            const string station = "VoxelEngine.EntityStation";
            const string entity = "VoxelEngine.ParentEntity";
            const string item = "Game.ItemInstanceData";
            const string list = "System.Collections.Generic.List`1<";
            var build = Method(module, page, "BuildStationItemsForSaleList", true, "System.Void");
            var open = Method(module, page, "OpenHubUI", true, "System.Void", entity, entity, "System.Boolean");
            var trade = Method(module, page, "OnTradeMade", true, "System.Void", item, "System.Single",
                "System.Single", "System.Int32", "System.Boolean");
            bool available = Field(module, page, "station", entity, true)
                && Field(module, station, "stationUltimateParent", station, false)
                && Field(module, station, "itemToSell", list + item + ">", false)
                && Field(module, station, "itemToSellPrices", list + "System.Single>", false)
                && Field(module, "VoxelEngine.Brick", "BRICK_INDEX", "VoxelEngine.Brick[]", true)
                && Method(module, item, "Register", false, "System.Int32")?.IsPublic == true
                && Method(module, "VoxelEngine.CraftingElement", "get_ToItemData", false, item)?.IsPublic == true
                && open?.IsPublic == true && Calls(open, page, "BuildStationItemsForSaleList")
                && Calls(open, page, "get_IsFuelCorporation") && Calls(open, page, "get_IsTradingCorporation")
                && UsesField(build, "itemToSell") && UsesField(build, "itemToSellPrices")
                && UsesField(trade, "itemToSell") && UsesField(trade, "itemToSellPrices");
            return new CapabilityResult(name, available, available
                ? "Native kiosk, stock display and transaction lists validated."
                : "Native kiosk, stock display or transaction lists changed.");

            static bool Calls(MethodDefinition? method, string type, string member)
                => method?.HasBody == true && method.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference reference && reference.DeclaringType.FullName == type && reference.Name == member);

            static bool UsesField(MethodDefinition? method, string member)
                => method?.HasBody == true && method.Body.Instructions.Any(instruction =>
                    instruction.Operand is FieldReference reference && reference.DeclaringType.FullName == station && reference.Name == member);
        }
    }
}
