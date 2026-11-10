using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using UI;
using UI.Codex;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sevo.Framework.Runtime
{
    /// <summary>Lists live registered items in the native Codex by their persistent registration keys.</summary>
    public static class ItemCodexRegistration
    {
        private static readonly Dictionary<UI_Codex, View> Views = new Dictionary<UI_Codex, View>();
        private static Action<string>? warning;
        private static FieldInfo blocksParent = null!;
        private static FieldInfo familyToSlots = null!;
        private static MethodInfo addItem = null!;
        private static MethodInfo startDragging = null!;

        public static bool IsAvailable => warning != null;

        internal static void Install(Harmony harmony, Action<string> warn)
        {
            blocksParent = AccessTools.DeclaredField(typeof(UI_Codex), "blocksParent");
            familyToSlots = AccessTools.DeclaredField(typeof(UI_Codex), "familyToSlotsBlocks");
            addItem = AccessTools.DeclaredMethod(typeof(ItemStorage), "AddItem");
            startDragging = AccessTools.DeclaredMethod(typeof(ItemSlot), "StartDraggingItem");

            harmony.Patch(AccessTools.DeclaredMethod(typeof(UI_Codex), nameof(UI_Codex.ButtonTabBlocksPressed), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(ItemCodexRegistration), nameof(AfterOpen)));
            PatchReset(typeof(ItemStorage), "OnDestroy", nameof(BeforeDestroy));
            PatchReset(typeof(ItemInstanceResources), nameof(ItemInstanceResources.OnGameReload), nameof(BeforeNativeReset));
            PatchReset(typeof(ItemInstanceResources), nameof(ItemInstanceResources.ReleaseResources), nameof(BeforeNativeReset));
            warning = warn;

            void PatchReset(Type type, string name, string hook) => harmony.Patch(
                AccessTools.DeclaredMethod(type, name, Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(ItemCodexRegistration), hook));
        }

        internal static void Disable()
        {
            BeforeNativeReset();
            warning = null;
        }

        private static bool NativeReady() => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && ItemInstanceResources.DatasForContext.IsCreated;

        private static void AfterOpen(UI_Codex __instance)
        {
            if (!IsAvailable || !NativeReady() || PlayerState.IsQuittingToDesktopOrTitle
                || PlayerState.sessionMode is PlayerState.SessionMode.host or PlayerState.SessionMode.client)
                return;
            try
            {
                var liveItems = new List<(string Key, ItemInstanceData Data)>();
                foreach (string key in ItemRegistration.GetPublishedKeys())
                    if (ItemRegistration.TryCreateItemData(key, 1, out var data))
                        liveItems.Add((key, data));

                if (Views.TryGetValue(__instance, out var previous))
                {
                    if (previous.Previews.Count == liveItems.Count
                        && previous.Previews.Zip(liveItems, (preview, item) => preview.IsCurrent(item.Key, item.Data)).All(current => current)
                        && previous.Slots.All(slot => slot != null && previous.FamilySlots.Contains(slot)))
                        return;
                    RemoveView(__instance);
                }

                if (liveItems.Count == 0) return;
                var view = FindCraftingFamily(__instance);
                Views.Add(__instance, view);
                foreach (var (key, item) in liveItems)
                {
                    var data = item;
                    // Production builds have no debug version table. A detached preview token also
                    // protects cleanup if the stock container recycles its numeric ID.
                    data.seed = unchecked((uint)Guid.NewGuid().GetHashCode()) | 1U;
                    int id = data.Register();
                    var preview = new Preview(key, data);
                    view.Previews.Add(preview);
                    preview.Wrapper = new WorldItemPlayer(id);
                    int before = view.Family.childCount;
                    try
                    {
                        addItem.Invoke(__instance, new object[] { preview.Wrapper, view.FamilySlots.Count, view.Family, 9, 74, 84 });
                    }
                    finally
                    {
                        for (int i = before; i < view.Family.childCount; i++)
                            view.Slots.Add((RectTransform)view.Family.GetChild(i));
                    }
                    if (view.Family.childCount != before + 1
                        || view.Family.GetChild(before).GetComponent<ItemSlot>() is not ItemSlot slot
                        || slot.itemId != id || !slot.isFreezed)
                        throw new InvalidOperationException("The native Crafting slot was not created as a frozen preview.");
                    slot.onClick = (_, source) => TryStartCreativeDrag(__instance, view, preview, source);
                    view.FamilySlots.Add((RectTransform)slot.transform);
                }
                // Stock filter owns placement, scrolling, and subsequent search updates.
                __instance.OnBlockFilterUpdate(false);
                warning!("Keel listed " + view.Previews.Count + " registered items in Bricks → Crafting.");
            }
            catch (Exception exception)
            {
                SafeRemoveView(__instance);
                warning!("Keel native item Codex listing skipped: " + exception);
            }
        }

        private static View FindCraftingFamily(UI_Codex codex)
        {
            if (blocksParent.GetValue(codex) is not Transform parent || parent == null
                || familyToSlots.GetValue(codex) is not Dictionary<GameObject, List<RectTransform>> map
                || UI_Codex.TABS_BLOCKS.Count(tab => ReferenceEquals(tab, CodexTab.CRAFT_ITEMS)) != 1)
                throw new InvalidOperationException("The stock Bricks Crafting family is unavailable.");
            string title = CodexTab.CRAFT_ITEMS.description;
            var family = map.SingleOrDefault(pair => pair.Key != null && pair.Key.transform.parent == parent
                && string.Equals(pair.Key.name, title, StringComparison.Ordinal));
            if (family.Key == null)
                throw new InvalidOperationException("The stock Bricks Crafting family is missing from its live layout.");
            return new View(family.Key.transform, family.Value);
        }

        private static void TryStartCreativeDrag(UI_Codex codex, View view, Preview preview, ItemSlot slot)
        {
            if (!IsAvailable || PlayerState.gameMode != PlayerState.GameMode.creative
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.InLoadingScreen
                || PlayerState.IsQuittingToDesktopOrTitle || Dependency.PlayerManager is not { IsLocal: true }
                || !NativeReady() || codex == null || UI_Codex.IsActive?.Value != true
                || UI_Codex.CurrentType != UI_Codex.TabsType.bricks || slot == null || !slot.gameObject.activeInHierarchy
                || !slot.isFreezed || slot.itemId != preview.Data.id || UIItemCursor.itemDraggedSlot == null
                || UIItemCursor.IsDraggingItem || !Views.TryGetValue(codex, out var current) || !ReferenceEquals(current, view)
                || !ItemRegistration.TryCreateItemData(preview.Key, 1, out var live) || !preview.IsCurrent(preview.Key, live))
                return;
            try { startDragging.Invoke(slot, null); }
            catch (Exception exception) { warning!("Keel could not start native Creative item dragging: " + exception); }
        }

        private static void BeforeDestroy(ItemStorage __instance)
        {
            if (__instance is UI_Codex codex) SafeRemoveView(codex);
        }

        private static void BeforeNativeReset()
        {
            foreach (var codex in Views.Keys.ToArray()) SafeRemoveView(codex);
        }

        private static void SafeRemoveView(UI_Codex codex)
        {
            try { RemoveView(codex); }
            catch (Exception exception) { warning?.Invoke("Keel item Codex cleanup failed: " + exception.GetBaseException().Message); }
        }

        private static void RemoveView(UI_Codex codex)
        {
            if (!Views.TryGetValue(codex, out var view)) return;
            Views.Remove(codex);
            var hovered = UI_CodexItemPreview.item_over;
            if (hovered != null && view.Previews.Any(preview => ReferenceEquals(preview.Wrapper, hovered.Value)))
                hovered.Value = Item.VOID_ITEM;
            bool nativeReady = NativeReady();
            foreach (var root in view.Slots)
            {
                view.FamilySlots.Remove(root);
                if (root == null) continue;
                root.gameObject.SetActive(false);
                if (nativeReady && root.GetComponent<ItemSlot>() is ItemSlot slot)
                    slot.SetItemStorageId(0, false, false);
                Object.Destroy(root.gameObject);
            }
            foreach (var preview in view.Previews)
            {
                int id = preview.Data.id;
                if (WorldItemPlayer.WorldItems.TryGetValue(id, out var wrapper) && ReferenceEquals(wrapper, preview.Wrapper))
                    WorldItemPlayer.WorldItems.Remove(id);
                if (nativeReady && preview.OwnsNativeId())
                    ItemInstanceResources.ReleaseItemId(id);
            }
        }

        private sealed class View
        {
            internal readonly Transform Family;
            internal readonly List<RectTransform> FamilySlots;
            internal readonly List<RectTransform> Slots = new List<RectTransform>();
            internal readonly List<Preview> Previews = new List<Preview>();

            internal View(Transform family, List<RectTransform> familySlots)
            {
                Family = family;
                FamilySlots = familySlots;
            }
        }

        private sealed class Preview
        {
            internal readonly string Key;
            internal readonly ItemInstanceData Data;
            internal WorldItemPlayer? Wrapper;

            internal Preview(string key, ItemInstanceData data)
            {
                Key = key;
                Data = data;
            }

            internal bool IsCurrent(string key, ItemInstanceData current) => key == Key
                && current.prefabId == Data.prefabId && current.itemType == Data.itemType
                && current.contextualDataId == Data.contextualDataId && OwnsNativeId();

            internal bool OwnsNativeId()
            {
                var items = ItemInstanceResources.DatasForContext;
                if (!items.IsCreated || Data.id <= 0 || Data.id >= items.Length) return false;
                var live = ItemInstanceResources.GetItemData(Data.id);
                return live.id == Data.id && live.prefabId == Data.prefabId && live.itemType == Data.itemType
                    && live.contextualDataId == Data.contextualDataId && live.seed == Data.seed;
            }
        }
    }
}
