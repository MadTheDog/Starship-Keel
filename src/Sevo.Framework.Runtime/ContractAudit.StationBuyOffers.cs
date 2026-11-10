using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateStationBuyOffers(ModuleDefinition module)
        {
            const string name = "stationBuyOffers";
            const string page = "Game.UI_StationLandingPage";
            const string item = "Game.ItemInstanceData";
            const string entity = "VoxelEngine.ParentEntity";
            var price = Method(module, "Game.ItemEconomy", "GetItemPrice", true, "System.Single",
                "World.StarData", item, "System.Boolean", entity);
            var sell = Method(module, page, "TryAddTradeableItem", true, "System.Void",
                item, "VoxelEngine.BrickCoord", "System.Int32");
            var trade = Method(module, page, "OnTradeMade", true, "System.Void", item, "System.Single",
                "System.Single", "System.Int32", "System.Boolean");
            bool available = price?.HasBody == true && HasPriceCall(sell, OpCodes.Ldc_I4_0)
                && HasPriceCall(trade, OpCodes.Ldc_I4_1) && trade!.Parameters[2].Name == "price"
                && !trade.Body.HasExceptionHandlers
                && Field(module, "World.StarData", "seed", "System.UInt64", false)
                && Field(module, entity, "SGUID", "System.String", false)
                && Field(module, "VoxelEngine.EntityStation", "StationType", "Game.StationType", false)
                && Field(module, item, "itemType", "Game.WorldItemType", false)
                && Method(module, entity, "get_EntityStation", false, "VoxelEngine.EntityStation")?.IsPublic == true;
            return new CapabilityResult(name, available, available
                ? "Native station sell-list, resale pricing and item identity validated."
                : "Native station sell-list, resale pricing or item identity changed.");

            bool HasPriceCall(MethodDefinition? method, OpCode direction)
            {
                if (method?.HasBody != true) return false;
                var calls = method.Body.Instructions.Where(instruction =>
                    instruction.OpCode == OpCodes.Call
                    && instruction.Operand is MethodReference target && target.FullName == price!.FullName).ToArray();
                return calls.Length == 1 && calls[0].Previous?.Previous?.OpCode == direction;
            }
        }
    }
}
