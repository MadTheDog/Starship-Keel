using System.Linq;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateSurfaceSites(ModuleDefinition module)
        {
            const string name = "surfaceSites";
            const string grid = "VoxelEngine.ParentEntity";
            const string data = "EntityInstanceData";
            bool available = EvaluateTerrainAnchorAttachments(module).Available
                && Method(module, "Player_NetworkSetup", "ServerLoadBlueprint", false, grid, data, "WorldTransform", "World.StarData")?.IsPublic == true
                && Method(module, "Manager.Save.EntityInstanceSystem_mp", "GetPlayerInstanceFullPath", true, "System.String",
                    "System.String", "System.String")?.IsPublic == true
                && Method(module, grid, "RegisterOnInitializedCallback", false, "System.Void", "System.Action")?.IsPublic == true
                && Field(module, data, "solarBodyId", "System.Int32", false)
                && Field(module, data, "saveInWorld", "System.Boolean", false)
                && Field(module, data, "entityType", "VoxelEngine.EntityType", false)
                && Field(module, "VoxelEngine.EntityRigidbody", "IsForcedStaticSelf", "Common.BoolEventVariable", false);
            return new CapabilityResult(name, available, available
                ? "Native surface anchors, save identity and blueprint load validated."
                : "Native surface anchor, save identity or blueprint load changed.");
        }

        public static CapabilityResult EvaluateGeneratedPoiLoot(ModuleDefinition module)
        {
            const string name = "generatedPoiLoot";
            const string grid = "VoxelEngine.ParentEntity";
            const string item = "Game.ItemInstanceData";
            var native = module.GetType(grid)?.NestedTypes.SingleOrDefault(type =>
                type.Fields.Any(field => field.Name == "<>4__this" && field.FieldType.FullName == grid)
                && type.Name.StartsWith("<SetInitialized>d__", System.StringComparison.Ordinal));
            var advance = native?.Methods.SingleOrDefault(method => method.Name == "MoveNext");
            bool available = Method(module, grid, "SetInitialized", false, "System.Void")?.HasBody == true
                && Field(module, grid, "isNewInstance", "System.Boolean", false)
                && Field(module, grid, "SGUID", "System.String", false)
                && Field(module, grid, "OnEntityInitialized", "System.Action`1<" + grid + ">", false)
                && advance?.HasBody == true && advance.Body.Instructions.Any(instruction => instruction.Operand is FieldReference reference
                    && reference.DeclaringType.FullName == grid && reference.Name == "OnEntityInitialized")
                && Method(module, "VoxelEngine.StorageCommon", "InsertUnregisteredItemOrMerge", true, "System.Boolean",
                    "System.Int32", item, "VoxelEngine.StorageContext", "System.Int32")?.IsPublic == true;
            return new CapabilityResult(name, available, available
                ? "Native fresh-entity initialization and unregistered item insertion validated."
                : "Native fresh-entity initialization or storage insertion changed.");
        }
    }
}
