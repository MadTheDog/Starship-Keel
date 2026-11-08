using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal static partial class MachineRecipeResources
    {
        private static readonly Dictionary<(MachineKind Kind, int Index), MachineRoutes> OwnedRoutes =
            new Dictionary<(MachineKind, int), MachineRoutes>();

        private static IReadOnlyList<ItemRequestData> BuildRoutes(MachineNativeState machine,
            MachineRecipeDefinition[] list, StorageContainerData storage, StorageContext context)
        {
            if (!machine.PullInput && !machine.PushOutput) return Array.Empty<ItemRequestData>();
            if (!ReadInventory(storage, context, out var items)) return Array.Empty<ItemRequestData>();
            var requests = new List<ItemRequestData>();
            foreach (var recipe in list)
            {
                if (machine.PullInput && machine.InputItemId == 0 && machine.IsIdle
                    && CanFit(storage, recipe.Input.ToItemData, context)
                    && CanFit(storage, recipe.Output.ToItemData, context,
                        recipe.Input.ToItemData.GetVolumeInLiter(context)))
                {
                    var available = items.Where(item => MachineRecipeDefinition.Matches(recipe.Input, item))
                        .Select(item => item.quantity).DefaultIfEmpty(0f).Max();
                    if (available < recipe.Input.quantity)
                        requests.Add(CreateRequest(recipe.Input, RequestType.pull, recipe.Input.quantity - available));
                }
                if (machine.PushOutput)
                {
                    var count = items.Where(item => MachineRecipeDefinition.Matches(recipe.Output, item)).Sum(item => item.quantity);
                    if (count <= 0f || !float.IsFinite(count)) continue;
                    var request = CreateRequest(recipe.Output, RequestType.push, count);
                    if (!requests.Any(existing => existing.IsMatching(request))) requests.Add(request);
                }
            }
            return requests;
        }

        private static ItemRequestData CreateRequest(CraftingElement element, RequestType type, float quantity) =>
            new ItemRequestData
            {
                itemType = element.itemType,
                prefabId = MachineRecipeDefinition.UsesMaterialContext(element.itemType) ? 0 : element.id,
                contextualDataId = element.contextualDataId,
                itemFlags = element.itemFlags,
                quantity = quantity,
                requestType = type,
                // The stock job enforces transfer mode, machine pull exclusion, category, network and filters.
                targetType = RequestTargetType.any
            };

        private static void ReconcileRoutes(MachineNativeState machine, IReadOnlyList<ItemRequestData> wanted,
            StorageContext context)
        {
            var key = (machine.Kind, machine.NativeIndex);
            if (!OwnedRoutes.TryGetValue(key, out var routes) || !routes.Owns(machine))
            {
                if (wanted.Count == 0) return;
                routes = new MachineRoutes(machine);
                OwnedRoutes[key] = routes;
            }
            routes.Frame = Time.frameCount;
            if (!TryReadStorage(machine, context, out var storage) || !ReadRequests(storage, context, out var existing))
                return;

            foreach (var old in routes.Requests.ToArray())
            {
                if (!existing.TryGetValue(old.Key, out var current) || !IsSameRoute(current, old.Value))
                {
                    routes.Requests.Remove(old.Key);
                    continue;
                }
                if (wanted.Any(request => IsSameRoute(current, request))) continue;
                StorageCommon.RemoveRequest(storage.id, old.Key, context);
                routes.Requests.Remove(old.Key);
                existing.Remove(old.Key);
            }
            foreach (var request in wanted)
            {
                // Preserve stock or other mods' exact requests. Only refresh entries this adapter allocated.
                if (existing.Values.Any(value => value.IsMatching(request) && value.quantity >= request.quantity
                    && value.targetType != RequestTargetType.any)) continue;
                var match = existing.FirstOrDefault(pair => IsSameRoute(pair.Value, request));
                if (match.Key != 0)
                {
                    if (routes.Requests.ContainsKey(match.Key) && match.Value.quantity != request.quantity)
                    {
                        var updated = match.Value;
                        updated.quantity = request.quantity;
                        context.SetRequestData(updated, match.Key, Operation);
                    }
                    continue;
                }
                var mutable = context.GetStorageDataForMutation(storage.id);
                var entry = request;
                entry.nextElementId = mutable.requestId;
                mutable.requestId = context.AllocateRequest(entry, Operation);
                context.SetStorageData(mutable, storage.id, Operation);
                routes.Requests.Add(mutable.requestId, entry);
                existing.Add(mutable.requestId, entry);
            }
            if (routes.Requests.Count == 0) OwnedRoutes.Remove(key);
        }

        private static bool ReadRequests(StorageContainerData storage, StorageContext context,
            out Dictionary<int, ItemRequestData> requests)
        {
            requests = new Dictionary<int, ItemRequestData>();
            var live = StorageResources.RequestDatasForContext;
            for (int id = storage.requestId; id != 0;)
            {
                if (!live.IsCreated || id < 0 || id >= live.Length
                    || requests.Count >= context.RequestChainGuard || requests.ContainsKey(id)) return false;
                var request = context.GetRequestData(id);
                requests.Add(id, request);
                id = request.nextElementId;
            }
            return true;
        }

        private static bool IsSameRoute(ItemRequestData left, ItemRequestData right) =>
            left.IsMatching(right) && left.targetType == right.targetType && left.parentMode == right.parentMode;

        private static void PruneRoutes(StorageContext context)
        {
            foreach (var entry in OwnedRoutes.ToArray())
            {
                if (entry.Value.Frame == Time.frameCount) continue;
                if (MachineNativeState.TryRead(entry.Key.Kind, entry.Key.Index, out var machine)
                    && entry.Value.Owns(machine))
                    ReconcileRoutes(machine, Array.Empty<ItemRequestData>(), context);
                else
                    OwnedRoutes.Remove(entry.Key);
            }
        }

        private static void ClearOwnedRoutes(StorageContext context)
        {
            foreach (var entry in OwnedRoutes.ToArray())
                if (MachineNativeState.TryRead(entry.Key.Kind, entry.Key.Index, out var machine)
                    && entry.Value.Owns(machine))
                    ReconcileRoutes(machine, Array.Empty<ItemRequestData>(), context);
            OwnedRoutes.Clear();
        }

        private sealed class MachineRoutes
        {
            private readonly int storageId;
            private readonly int commandId;
            private readonly BrickCoord placement;
            internal readonly Dictionary<int, ItemRequestData> Requests = new Dictionary<int, ItemRequestData>();
            internal int Frame;

            internal MachineRoutes(MachineNativeState machine)
            {
                storageId = machine.StorageId;
                commandId = machine.CommandId;
                placement = machine.Placement;
            }

            internal bool Owns(MachineNativeState machine) => machine.StorageId == storageId
                && machine.CommandId == commandId && machine.Placement.Equals(placement);
        }
    }
}
