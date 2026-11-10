using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateItemCodex(ModuleDefinition module)
        {
            const string capability = "itemCodex";
            const string codex = "UI.UI_Codex", storage = "UI.ItemStorage", tab = "UI.Codex.CodexTab";
            const string slot = "ItemSlot", transform = "UnityEngine.Transform", gameObject = "UnityEngine.GameObject";
            const string itemData = "Game.ItemInstanceData", nativeItems = "Game.ItemInstanceResources";
            const string wrapper = "Game.WorldItemPlayer";
            const string i32 = "System.Int32", boolean = "System.Boolean", none = "System.Void";
            const string tabs = "System.Collections.Generic.List`1<" + tab + ">";
            const string familyMap = "System.Collections.Generic.Dictionary`2<UnityEngine.GameObject,System.Collections.Generic.List`1<UnityEngine.RectTransform>>";
            try
            {
                var open = RequireItemMethod(module, codex, "ButtonTabBlocksPressed", false, none);
                var awake = RequireItemMethod(module, codex, "Awake", false, none);
                var create = RequireItemMethod(module, storage, "CreateSlots", false, i32, tabs, transform,
                    i32, i32, i32, i32, familyMap, "UnityEngine.RectTransform", gameObject, codex);
                var add = RequireItemMethod(module, storage, "AddItem", false, none, "Item", i32, transform, i32, i32, i32);
                var addFamily = RequireItemMethod(module, storage, "AddFamily", false, gameObject, tab, i32 + "&",
                    transform, i32, i32, i32, familyMap, "UnityEngine.RectTransform", gameObject, codex);
                if (!CodexField(module, codex, "blocksParent", transform, false)
                    || !CodexField(module, codex, "familyToSlotsBlocks", familyMap, false)
                    || !CodexField(module, storage, "SeparatorSize", i32, false)
                    || !CodexField(module, tab, "items", "System.Collections.Generic.List`1<Item>", false)
                    || !UsesCodexField(open, codex, "blocksParent")
                    || !CallsCodex(open, codex, "Activate") || !CallsCodex(create, storage, "AddFamily")
                    || !UsesCodexField(create, storage, "families") || !UsesCodexField(create, storage, "SeparatorSize")
                    || !CallsCodex(create, "UnityEngine.RectTransform", "set_sizeDelta")
                    || !UsesCodexField(addFamily, tab, "description") || !CallsCodex(addFamily, storage, "AddItem")
                    || !CallsCodex(addFamily, "UnityEngine.Object", "set_name") || !CallsCodex(addFamily, familyMap, "set_Item"))
                    throw new InvalidOperationException("The native Bricks family layout changed.");

                var layout = awake.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Ldsfld
                    && instruction.Operand is FieldReference field && field.DeclaringType.FullName == codex
                    && field.Name == "TABS_BLOCKS" && field.FieldType.FullName == tabs);
                var layoutArgs = awake.Body.Instructions.Skip(awake.Body.Instructions.IndexOf(layout) + 1).Take(15).ToArray();
                var filter = RequireItemMethod(module, codex, "OnBlockFilterUpdate", false, none, boolean);
                if (layout == null || layoutArgs.Length != 15 || layoutArgs[0].OpCode.Code != Code.Ldarg_0
                    || layoutArgs[1].Operand is not FieldReference parent || parent.Name != "blocksParent"
                    || Constant(layoutArgs[2]) != 0 || Constant(layoutArgs[3]) != 9 || Constant(layoutArgs[4]) != 74
                    || Constant(layoutArgs[5]) != 84 || layoutArgs[7].Operand is not FieldReference map
                    || map.Name != "familyToSlotsBlocks" || layoutArgs[11].OpCode.Code != Code.Ldarg_0
                    || layoutArgs[14].Operand is not MethodReference createCall || createCall.FullName != create.FullName
                    || !UsesCodexField(filter, codex, "familyToSlotsBlocks")
                    || !CallsCodex(filter, storage, "FilterItemSlots"))
                    throw new InvalidOperationException("The stock Bricks slot layout or filter changed.");

                var tabInit = RequireItemMethod(module, tab, ".cctor", true, none);
                var crafting = tabInit.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Stsfld
                    && instruction.Operand is FieldReference field && field.Name == "CRAFT_ITEMS");
                if (crafting?.Previous?.OpCode.Code != Code.Newobj
                    || crafting.Previous.Previous?.Operand is not FieldReference list || list.DeclaringType.FullName != codex
                    || list.Name != "TABS_BLOCKS" || !CodexField(module, tab, "CRAFT_ITEMS", tab, true)
                    || !CodexField(module, tab, "description", "Manager.LocalizedString", false))
                    throw new InvalidOperationException("The stock Crafting category no longer belongs to Bricks.");
                var click = RequireItemMethod(module, slot, "OnClick", false, none);
                var drag = RequireItemMethod(module, slot, "StartDraggingItem", false, none);
                var drop = RequireItemMethod(module, slot, "DropItem", false, none);
                if (!UsesCodexField(drag, slot, "isFreezed") || !UsesCodexField(drag, "UIItemCursor", "itemDraggedSlot")
                    || !CallsCodex(drag, "Game.ItemInstanceCommon", "DuplicateItem")
                    || !CallsCodex(drag, slot, "SetItemStorageId")
                    || !UsesCodexField(drop, "UIItemCursor", "itemDraggedSlot")
                    || !CallsCodex(drop, slot, "CanDropToInventory") || !CallsCodex(drop, slot, "SetItemStorageId"))
                    throw new InvalidOperationException("The native frozen Creative drag or destination validation changed.");
                var update = RequireItemMethod(module, slot, "Update", false, none);
                var set = RequireItemMethod(module, slot, "SetItemStorageId", false, none, i32, boolean, boolean);
                var icon = RequireItemMethod(module, slot, "ComputeWorldItemTexture", false, none);
                var preview = RequireItemMethod(module, "UI.UI_CodexItemPreview", "OnNewItemOver", false, none);
                var name = RequireItemMethod(module, wrapper, "get_TranslatedName", false, "System.String");
                var description = RequireItemMethod(module, wrapper, "get_TranslatedDescription", false, "System.String");
                var wrapperConstructor = RequireItemMethod(module, wrapper, ".ctor", false, none, i32);
                if (!wrapperConstructor.IsPublic || !CodexField(module, wrapper, "WorldItems",
                    "System.Collections.Generic.Dictionary`2<System.Int32,Game.WorldItemPlayer>", true))
                    throw new InvalidOperationException("The native preview wrapper or its ownership table changed.");
                var from = RequireItemMethod(module, itemData, "FromItem", true, itemData, "Item");
                var reuse = from.Body.Instructions.Take(8).ToArray();
                if (reuse.Length != 8 || reuse[0].OpCode.Code != Code.Ldarg_0 || reuse[1].OpCode.Code != Code.Isinst
                    || reuse[1].Operand is not TypeReference wrapperType || wrapperType.FullName != wrapper
                    || reuse[2].OpCode.Code != Code.Stloc_0 || reuse[3].OpCode.Code != Code.Ldloc_0
                    || reuse[4].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || reuse[4].Operand != reuse[7].Next || reuse[5].OpCode.Code != Code.Ldloc_0
                    || !CallsCodex(reuse[6], wrapper, "get_ItemData") || reuse[7].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native item slots no longer reuse detached world-item previews.");
                var clickInvoke = click.Body.Instructions.FirstOrDefault(instruction => instruction.OpCode.Code == Code.Callvirt
                    && instruction.Operand is MethodReference call && call.Name == "Invoke"
                    && call.DeclaringType.FullName == "System.Action`2<System.Int32,ItemSlot>");
                var clickField = click.Body.Instructions.FirstOrDefault(instruction => instruction.OpCode.Code == Code.Ldfld
                    && instruction.Operand is FieldReference field && field.DeclaringType.FullName == slot && field.Name == "onClick");
                var clickBranch = clickField?.Next;
                var updateFrozen = update.Body.Instructions.Take(6).LastOrDefault();
                if (!set.IsPublic || !CallsCodex(add, itemData, "FromItem") || !CallsCodex(add, slot, set.Name)
                    || !add.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stfld
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == slot
                        && field.Name == "isFreezed" && instruction.Previous?.OpCode.Code == Code.Ldc_I4_1)
                    || clickBranch?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || clickBranch.Operand is not Instruction defaultClick || defaultClick != clickInvoke?.Next?.Next
                    || clickInvoke?.Next?.OpCode.Code != Code.Ret
                    || click.Body.Instructions.Take(click.Body.Instructions.IndexOf(clickInvoke))
                        .Any(instruction => CallsCodex(instruction, slot, "StartDraggingItem") || CallsCodex(instruction, slot, "DropItem"))
                    || !UsesCodexField(update, slot, "isFreezed") || updateFrozen?.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                    || !CallsCodex(icon, itemData, "SetRenderData")
                    || !CallsCodex(preview, "Item", "get_TranslatedName") || !CallsCodex(preview, "Item", "get_TranslatedDescription")
                    || !CallsCodex(name, itemData, "Name") || !CallsCodex(description, itemData, "Description"))
                    throw new InvalidOperationException("The native slot callback or live item preview path changed.");

                RequireItemMethod(module, storage, "OnDestroy", false, none);
                RequireItemMethod(module, nativeItems, "OnGameReload", true, none);
                RequireItemMethod(module, nativeItems, "ReleaseResources", true, none);
                RequireItemMethod(module, nativeItems, "ReleaseItemId", true, none, i32, i32, "Game.ItemOperationType", boolean);
                if (!Field(module, itemData, "seed", "System.UInt32", false))
                    throw new InvalidOperationException("The native detached preview identity token changed.");
                var register = RequireItemMethod(module, itemData, "Register", false, i32);
                if (!register.IsPublic || !CallsCodex(register, nativeItems, "GetItemId") || !CallsCodex(register, nativeItems, "SetItemData"))
                    throw new InvalidOperationException("The native detached item preview allocation path changed.");

                return new CapabilityResult(capability, true, "Native Bricks Crafting layout, live previews and frozen Creative drag validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool CodexField(ModuleDefinition module, string owner, string name, string type, bool isStatic) =>
            module.GetType(owner)?.Fields.Any(field => field.Name == name && field.FieldType.FullName == type
                && field.IsStatic == isStatic) == true;

        private static bool UsesCodexField(MethodDefinition method, string owner, string field) =>
            method.Body.Instructions.Any(instruction => instruction.Operand is FieldReference reference
                && reference.DeclaringType.FullName == owner && reference.Name == field);

        private static bool CallsCodex(MethodDefinition method, string owner, string name) =>
            method.Body.Instructions.Any(instruction => CallsCodex(instruction, owner, name));

        private static bool CallsCodex(Instruction instruction, string owner, string name) =>
            instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference reference
                && reference.DeclaringType.FullName == owner && reference.Name == name;
    }
}
