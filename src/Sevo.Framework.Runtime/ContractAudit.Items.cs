using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string ItemDataType = "Game.ItemInstanceData";
        private const string ItemSaveType = "Game.ItemSaveData";
        private const string PrefabDataType = "Game.PrefabData";
        private const string WorldPropType = "World.WorldProp";
        private const string ItemRenderResourcesType = "Game.ItemInstanceRenderResources";
        private const string ItemPrefabArrayType = "Unity.Collections.NativeArray`1<" + PrefabDataType + ">";

        public static CapabilityResult EvaluateItems(ModuleDefinition module)
        {
            const string capability = "nativeItems";
            try
            {
                var repo = RequireItemMethod(module, ItemRenderResourcesType, "OnRepoLoaded", true, "System.Void", "System.Boolean");
                var publish = UniqueCall(repo, "World.WorldPropsCollection", "RegisterRenderData");
                var recipes = UniqueCall(repo, "VoxelEngine.CraftingSchematic", "AllocateResources");
                if (publish >= recipes
                    || !Field(module, ItemRenderResourcesType, "WorldProps", WorldPropType + "[]", true)
                    || !Field(module, ItemRenderResourcesType, "PrefabDatas", ItemPrefabArrayType, true)
                    || !Field(module, "World.WorldPropsResources", "worldPropsIndex", WorldPropType + "[]", true)
                    || !Field(module, "VoxelEngine.StorageContext", "prefabDatas", ItemPrefabArrayType, false))
                    throw new InvalidOperationException("Native world-item table or resource ordering changed.");

                var stockPublish = RequireItemMethod(module, "World.WorldPropsCollection", "RegisterRenderData", false, "System.Void");
                var worldLoad = RequireItemMethod(module, "World.WorldPropsResources", "LoadResources", false, "System.Void", "System.Boolean");
                var render = RequireItemMethod(module, ItemDataType, "SetRenderData", false, "System.Void",
                    "VoxelEngine.EvoRenderer", "System.Single", "System.Int32", "System.Boolean");
                var storage = RequireItemMethod(module, "VoxelEngine.StorageCommon", "get_CONTEXT", true, "VoxelEngine.StorageContext");
                if (!ItemFieldAccess(stockPublish, ItemRenderResourcesType, "PrefabDatas")
                    || !ItemFieldAccess(stockPublish, ItemRenderResourcesType, "WorldProps")
                    || !ItemFieldAccess(render, ItemRenderResourcesType, "PrefabDatas")
                    || ItemFieldAccess(render, ItemRenderResourcesType, "BrickPrefabDatas")
                    || !ItemFieldAccess(storage, ItemRenderResourcesType, "PrefabDatas")
                    || !ItemFieldAccess(worldLoad, "World.WorldPropsResources", "propMeshDataBuffer")
                    || !worldLoad.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldstr
                        && Equals(instruction.Operand, "WorldPropMeshDataBuffer")))
                    throw new InvalidOperationException("A stock rendering or storage path stopped using the world-item table.");

                var volume = RequireItemMethod(module, ItemDataType, "GetVolumePrefab", false, "System.Single", "VoxelEngine.StorageContext");
                var pickup = RequireItemMethod(module, ItemDataType, "CanBePickedUp", false, "System.Boolean", "VoxelEngine.StorageContext");
                var lookup = RequireItemMethod(module, ItemDataType, "GetPrefabData", false, PrefabDataType);
                var collider = RequireItemMethod(module, ItemDataType, "AllocateCollider", false, "System.Void", "System.Boolean");
                var name = RequireItemMethod(module, ItemDataType, "Name", false, "System.String", "System.Boolean", "System.Boolean");
                var description = RequireItemMethod(module, ItemDataType, "Description", false, "System.String");
                if (!ItemFieldAccess(volume, "VoxelEngine.StorageContext", "prefabDatas")
                    || !ItemFieldAccess(pickup, "VoxelEngine.StorageContext", "prefabDatas")
                    || !ItemFieldAccess(lookup, ItemRenderResourcesType, "PrefabDatas")
                    || !ItemFieldAccess(collider, PrefabDataType, "shape")
                    || !ItemFieldAccess(collider, PrefabDataType, "triangleTreeId")
                    || !ItemFieldAccess(name, ItemRenderResourcesType, "WorldProps")
                    || !ItemFieldAccess(description, ItemRenderResourcesType, "WorldProps"))
                    throw new InvalidOperationException("Native world-item metadata or pickup contracts changed.");

                var merge = RequireItemMethod(module, "Game.ItemInstanceCommon", "CanMerge", true, "System.Boolean",
                    ItemDataType + "&", ItemDataType, "VoxelEngine.StorageContext", "System.Boolean");
                if (!ItemFieldAccess(merge, ItemDataType, "prefabId") || !ItemFieldAccess(merge, ItemDataType, "itemType"))
                    throw new InvalidOperationException("Native item stacking no longer uses the prefab identity.");

                var save = RequireItemMethod(module, ItemDataType, "get_SaveData", false, ItemSaveType);
                var load = RequireItemMethod(module, ItemDataType, "LoadSaveData", true, ItemDataType,
                    ItemSaveType, "System.Int32", "System.Boolean");
                if (!Field(module, ItemSaveType, "data", "System.Byte[]", false)
                    || !ItemFieldAccess(save, ItemSaveType, "id") || !ItemFieldAccess(load, ItemSaveType, "id")
                    || !ItemFieldAccess(save, ItemSaveType, "itemType") || !ItemFieldAccess(load, ItemSaveType, "itemType")
                    || !OnlyEntityUsesItemPayload(module, save) || !OnlyEntityUsesItemPayload(module, load))
                    throw new InvalidOperationException("Native world-item persistence contracts changed.");
                const string formatter = "MessagePack.Formatters.Game.ItemSaveDataFormatter";
                var serialize = RequireItemMethod(module, formatter, "Serialize", false, "System.Void",
                    "MessagePack.MessagePackWriter&", ItemSaveType, "MessagePack.MessagePackSerializerOptions");
                var deserialize = RequireItemMethod(module, formatter, "Deserialize", false, ItemSaveType,
                    "MessagePack.MessagePackReader&", "MessagePack.MessagePackSerializerOptions");
                if (!ItemFieldAccess(serialize, ItemSaveType, "data") || !ItemFieldAccess(deserialize, ItemSaveType, "data"))
                    throw new InvalidOperationException("The stock item formatter no longer preserves the identity payload.");

                if (!ItemPrivateField(module, "worldPropId", "World.WorldPropCommon/WorldPropId")
                    || !ItemPrivateField(module, "nameInShader", "System.String")
                    || !ItemPrivateField(module, "description", "System.String")
                    || !ItemPrivateField(module, "mesh", "UnityEngine.Mesh"))
                    throw new InvalidOperationException("Stock world-prop metadata fields changed.");

                var reload = RequireItemMethod(module, ItemRenderResourcesType, "OnGameReload", true, "System.Void");
                if (ItemFieldAccess(reload, ItemRenderResourcesType, "WorldProps")
                    || ItemFieldAccess(reload, ItemRenderResourcesType, "PrefabDatas"))
                    throw new InvalidOperationException("World-item table lifetime changed during resource reload.");
                RequireItemMethod(module, ItemRenderResourcesType, "ReleaseResources", true, "System.Void");
                const string taskBool = "System.Threading.Tasks.Task`1<System.Boolean>";
                RequireItemMethod(module, "Manager.SteamLobbyManager", "OpenLobby", true, taskBool, "Network.MultiplayerLobbyVisibility");
                RequireItemMethod(module, "Manager.SteamLobbyManager", "JoinFriendsLobby", true, taskBool, "Manager.SteamLobbyInfo");

                return new CapabilityResult(capability, true, "Native stock-backed item tables and solo persistence entry points validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateContextualItems(ModuleDefinition module)
        {
            const string capability = "contextualItems";
            try
            {
                const string element = "VoxelEngine.CraftingElement";
                const string category = "Game.WorldItemType";
                const string context = "contextualDataId";
                const string integer = "System.Int32";
                if (!Field(module, ItemDataType, context, integer, false)
                    || !Field(module, ItemSaveType, context, integer, false)
                    || !Field(module, element, context, integer, false)
                    || !Field(module, PrefabDataType, "itemType", category, false)
                    || !module.GetType(category).Fields.Any(field => field.IsLiteral && field.Name == "ore")
                    || !module.GetType(category).Fields.Any(field => field.IsLiteral && field.Name == "ingot"))
                    throw new InvalidOperationException("Native material identity fields changed.");
                RequireItemMethod(module, "World.Terrain.TerrainMaterialCommon", "GetMaterialName", true,
                    "System.String", integer, integer);
                var create = RequireItemMethod(module, element, "get_ToItemData", false, ItemDataType);
                var merge = RequireItemMethod(module, "Game.ItemInstanceCommon", "CanMerge", true, "System.Boolean",
                    ItemDataType + "&", ItemDataType, "VoxelEngine.StorageContext", "System.Boolean");
                var contextualMerge = RequireItemMethod(module, "Game.ItemInstanceCommon", "CanMergeByContextualData",
                    true, "System.Boolean", ItemDataType, ItemDataType, "System.Boolean");
                var comparison = contextualMerge.Body.Instructions.TakeLast(6).ToArray();
                if (comparison.Length != 6 || comparison[0].OpCode.Code != Code.Ldarg_0
                    || !ItemFieldAccess(comparison[1], ItemDataType, context)
                    || comparison[2].OpCode.Code != Code.Ldarg_1
                    || !ItemFieldAccess(comparison[3], ItemDataType, context)
                    || comparison[4].OpCode.Code != Code.Ceq || comparison[5].OpCode.Code != Code.Ret
                    || !merge.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Call
                        && instruction.Operand is MethodReference target && target.FullName == contextualMerge.FullName))
                    throw new InvalidOperationException("Native ore stacking no longer compares both material contexts.");
                var save = RequireItemMethod(module, ItemDataType, "get_SaveData", false, ItemSaveType);
                var load = RequireItemMethod(module, ItemDataType, "LoadSaveData", true, ItemDataType,
                    ItemSaveType, integer, "System.Boolean");
                if (!ItemFieldAccess(create, element, context) || !ItemFieldAccess(create, ItemDataType, context)
                    || !ItemFieldAccess(merge, ItemDataType, context)
                    || !ItemFieldAccess(save, ItemSaveType, context) || !ItemFieldAccess(save, ItemDataType, context)
                    || !ItemFieldAccess(load, ItemSaveType, context) || !ItemFieldAccess(load, ItemDataType, context))
                    throw new InvalidOperationException("Native creation, stacking or save no longer carries material context.");
                return new CapabilityResult(capability, true, "Native contextual item creation, stacking and save fields validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static MethodDefinition RequireItemMethod(ModuleDefinition module, string type, string name,
            bool isStatic, string result, params string[] parameters)
        {
            var method = Method(module, type, name, isStatic, result, parameters);
            return method?.HasBody == true ? method
                : throw new InvalidOperationException("Native item method unavailable: " + type + "." + name);
        }

        private static bool ItemFieldAccess(MethodDefinition method, string declaringType, string name)
            => method.Body.Instructions.Any(instruction => ItemFieldAccess(instruction, declaringType, name));

        private static bool ItemFieldAccess(Instruction instruction, string declaringType, string name)
            => (instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Stfld or Code.Ldsfld or Code.Ldsflda or Code.Stsfld)
                && instruction.Operand is FieldReference field && field.DeclaringType.FullName == declaringType
                && field.Name == name;

        private static bool OnlyEntityUsesItemPayload(ModuleDefinition module, MethodDefinition method)
        {
            var entityType = Convert.ToInt32(module.GetType("Game.WorldItemType").Fields.Single(field => field.Name == "entity").Constant);
            var body = method.Body.Instructions;
            var uses = body.Where(instruction => ItemFieldAccess(instruction, ItemSaveType, "data"))
                .Select(body.IndexOf).ToArray();
            if (uses.Length == 0) return false;

            // The entity comparison jumps over every use of the otherwise unclaimed payload.
            return body.Select((instruction, index) => (instruction, index)).Any(entry =>
                (entry.instruction.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S)
                && entry.index >= 2 && ItemFieldAccess(body[entry.index - 2], ItemDataType, "itemType")
                && Constant(body[entry.index - 1]) == entityType && entry.instruction.Operand is Instruction skip
                && uses.All(use => use > entry.index && use < body.IndexOf(skip)));
        }

        private static bool ItemPrivateField(ModuleDefinition module, string name, string fieldType)
            => module.GetType(WorldPropType)?.Fields.Any(field => field.Name == name
                && !field.IsStatic && !field.IsInitOnly && field.FieldType.FullName == fieldType) == true;
    }
}
