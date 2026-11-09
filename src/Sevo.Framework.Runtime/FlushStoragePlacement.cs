using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Common;
using Game;
using HarmonyLib;
using Manager;
using UI.Inventory;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Placement adapter for a stock-faced flush storage fitting.</summary>
    internal static class FlushStoragePlacement
    {
        private static readonly Dictionary<int, BrickPipe> Fittings = new Dictionary<int, BrickPipe>();
        private static readonly FieldInfo LabelsField = typeof(CodeInstruction).GetField("labels")!;
        private static readonly object Affected = new object();
        private static readonly List<DeferredBuild> deferredBuilds = new List<DeferredBuild>();
        private static ConditionalWeakTable<VoxelEntity, object> affectedGrids = new ConditionalWeakTable<VoxelEntity, object>();
        [ThreadStatic] private static PlacementCheck? check;
        [ThreadStatic] private static bool payingForBuild;
        private static bool enabled;
        private static bool reportPlacement;

        internal static bool CanRegister(BrickPipe template, ItemStorageType role)
        {
            if (!enabled || role != ItemStorageType.tube || !ReferenceEquals(template, TabStorage.TANK_JOIN)
                || !template.isTankJoin || TabStorage.STORAGE_UNIT.hasTank)
                return false;

            var ports = StorageAttachmentPoint.GetPoints(template.brickID, ItemStorageType.fuelPipe);
            return ports.Count == 2 && ports[0].gridLevelOffset == 0 && ports[1].gridLevelOffset == 0
                && ports[0].point.Equals(Vector3.zero) && ports[1].point.Equals(Vector3.zero)
                && ports[0].normal.Equals(Vector3.down) && ports[1].normal.Equals(Vector3.up);
        }

        internal static void Bind(BrickPipe fitting)
        {
            if (!enabled || fitting.isTankJoin || fitting.GetItemStorageType(null!, 0) != ItemStorageType.tube
                || !ReferenceEquals(Brick.BRICK_INDEX[fitting.brickID], fitting))
                throw new InvalidOperationException("Flush fitting requires a registered ordinary item connector.");
            Fittings[fitting.brickID] = fitting;
        }

        internal static void Install(Harmony harmony)
        {
            reportPlacement = Environment.GetEnvironmentVariable("SEVO_KEEL_FLUSH_STORAGE_PLACEMENT_DIAGNOSTICS") == "1";
            var query = typeof(SkyPhysics.QueryOverlapAabb);
            var collector = AccessTools.Method(query, nameof(SkyPhysics.QueryOverlapAabb.OnElementIntersect),
                new[] { typeof(BrickColliderBVH.Element), typeof(int), typeof(bool), typeof(int) });
            var builder = AccessTools.TypeByName("NetworkedBuilder");
            var rpc = AccessTools.Method(builder, "UserCode_RpcVoxelAction", new[] { typeof(byte[]), typeof(bool), typeof(int) });
            var preview = AccessTools.Method(typeof(ActionBuild), "IsColliding", Type.EmptyTypes);
            if (reportPlacement) Debug.Log("[Keel Flush] Installing collector.");
            harmony.Patch(collector, transpiler: Hook(nameof(PatchCollector)));
            if (reportPlacement) Debug.Log("[Keel Flush] Installing commit.");
            harmony.Patch(rpc, transpiler: Hook(nameof(PatchCommit)));
            if (reportPlacement) Debug.Log("[Keel Flush] Installing preview.");
            harmony.Patch(preview, prefix: Hook(nameof(BeforePreview)), postfix: Hook(nameof(AfterPreview)),
                finalizer: Hook(nameof(FinishPreview)));
            if (reportPlacement) Debug.Log("[Keel Flush] Installing inventory charge.");
            harmony.Patch(AccessTools.Method(typeof(ActionBuild), nameof(ActionBuild.ConsumeQuantityForBuild),
                new[] { typeof(BrickInstanceData), typeof(int) }), prefix: Hook(nameof(BeforeCharge)));
            if (reportPlacement) Debug.Log("[Keel Flush] Installing mirror validation.");
            harmony.Patch(AccessTools.Method(typeof(ActionSymmetry), "BuildBrick", new[] { typeof(VoxelActionData), typeof(int) }),
                prefix: Hook(nameof(BeforeMirror)), finalizer: Hook(nameof(FinishMirror)));
            if (reportPlacement) Debug.Log("[Keel Flush] Installing pipe lifecycle.");
            harmony.Patch(AccessTools.Method(typeof(BrickPipe), nameof(BrickPipe.OnEntityInitialized),
                new[] { typeof(BrickInstanceData), typeof(VoxelEntity), typeof(bool) }), postfix: Hook(nameof(AfterPipeAdded)));
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            reportPlacement = false;
            check = null;
            Fittings.Clear();
            deferredBuilds.Clear();
            affectedGrids = new ConditionalWeakTable<VoxelEntity, object>();
        }

        private static HarmonyMethod Hook(string method) => new HarmonyMethod(typeof(FlushStoragePlacement), method);

        private static bool BeforeCharge(BrickInstanceData __0, int __1, ref bool __result)
        {
            if (payingForBuild || !GameloopManager.IsMainThread || !PlayerState.IsSurvival || !ActionBuild.IsBuildValid)
                return true;
            var proposed = ActionBuild.BrickCoord;
            if (!SameBuildData(__0, proposed.brickData) || Prepare(proposed) is not { } placement) return true;

            // The stock primary click ignores this method's result. Its exact action must have a
            // receipt at commit, even when the native hotbar no longer contains enough items.
            deferredBuilds.RemoveAll(build => build.Entity == null);
            if (deferredBuilds.Count >= 64) return !placement.Fitting;
            int itemId = ItemInstanceResources.GetItemData(__1).id;
            deferredBuilds.Add(new DeferredBuild(placement.Entity, __0, itemId));
            __result = true;
            return false;
        }

        private static bool BeforeMirror(VoxelActionData __0, out bool __state)
        {
            __state = payingForBuild;
            payingForBuild = true;
            if (!PlayerState.IsSurvival || __0.type != VoxelActionType.build) return true;
            var proposed = __0.brickCoord;
            return Prepare(proposed) is not { } placement || CanApply(placement, proposed, mirrored: true);
        }

        private static Exception? FinishMirror(bool __state, Exception? __exception)
        {
            payingForBuild = __state;
            return __exception;
        }

        private static bool SameBuildData(BrickInstanceData a, BrickInstanceData b)
            => a == b && a.gridLevel == b.gridLevel && a.instanceId == b.instanceId;

        private static bool HasBuildItem(BrickInstanceData data, int id)
        {
            if (id <= 0 || !UI_ItemBar.ContainsItemId(id)) return false;
            var item = ItemInstanceResources.GetItemData(id);
            int required = data.GetQuantity();
            return item.id == id && item.itemType == WorldItemType.brick && item.prefabId == data.brickId
                && required > 0 && float.IsFinite(item.quantity) && item.quantity >= required;
        }

        private sealed class DeferredBuild
        {
            internal readonly VoxelEntity Entity;
            internal readonly BrickInstanceData Data;
            internal readonly int ItemId;

            internal DeferredBuild(VoxelEntity entity, BrickInstanceData data, int itemId)
            {
                Entity = entity;
                Data = data;
                ItemId = itemId;
            }

            internal bool Matches(BrickCoord proposed, int itemId)
                => ReferenceEquals(Entity, proposed.entity) && SameBuildData(Data, proposed.brickData) && ItemId == itemId;
        }

        private static IEnumerable<CodeInstruction> PatchCollector(IEnumerable<CodeInstruction> code)
        {
            var instructions = code.ToList();
            var native = AccessTools.Method(typeof(BrickProperties), nameof(BrickProperties.IsOverlapValid),
                new[] { typeof(BrickProperties), typeof(int), typeof(int) });
            int index = instructions.FindIndex(instruction => instruction.Calls(native));
            if (index < 7 || instructions.Count(instruction => instruction.Calls(native)) != 1
                || Labels(instructions[index]).Count != 0 || instructions[index].blocks.Count != 0
                || instructions[index - 1].operand is not FieldInfo transform || transform.Name != "transformId")
                throw new InvalidOperationException("The native collector overlap call shape changed.");

            instructions[index] = new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(FlushStoragePlacement), nameof(ScopedOverlap)));
            instructions.InsertRange(index, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1) });
            return instructions;
        }

        private static IEnumerable<CodeInstruction> PatchCommit(IEnumerable<CodeInstruction> code, ILGenerator generator)
        {
            var instructions = code.ToList();
            var builder = AccessTools.TypeByName("NetworkedBuilder");
            var native = AccessTools.Method(builder, "DoAction", new[] { typeof(VoxelActionData).MakeByRefType(), typeof(bool), typeof(int) });
            int call = instructions.FindIndex(instruction => instruction.Calls(native));
            int end = instructions.Count - 1;
            if (call < 4 || instructions.Count(instruction => instruction.Calls(native)) != 1
                || instructions[end].opcode != OpCodes.Ret || instructions[call - 4].opcode != OpCodes.Ldarg_0
                || (instructions[call - 3].opcode != OpCodes.Ldloca && instructions[call - 3].opcode != OpCodes.Ldloca_S)
                || instructions[call - 2].opcode != OpCodes.Ldarg_2 || instructions[call - 1].opcode != OpCodes.Ldarg_3
                || instructions.Skip(call - 4).Take(5).Any(instruction => instruction.blocks.Count != 0)
                || instructions.Skip(call - 3).Take(4).Any(instruction => Labels(instruction).Count != 0))
                throw new InvalidOperationException("The native placement commit call shape changed.");

            var reject = generator.DefineLabel();
            Labels(instructions[end]).Add(reject);
            var action = new CodeInstruction(instructions[call - 3].opcode, instructions[call - 3].operand);
            var originalLabels = Labels(instructions[call - 4]);
            foreach (var label in originalLabels) Labels(action).Add(label);
            originalLabels.Clear();
            instructions.InsertRange(call - 4, new[]
            {
                action,
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlushStoragePlacement), nameof(ShouldApply))),
                new CodeInstruction(OpCodes.Brfalse, reject)
            });
            return instructions;
        }

        // The shipped Harmony API exposes Mono's Label through mscorlib. Use its non-generic list view.
        private static IList Labels(CodeInstruction instruction) => (IList)LabelsField.GetValue(instruction)!;

        private static void AfterPipeAdded(BrickInstanceData __0, VoxelEntity __1)
        {
            if (IsFitting(__0.brickId) && __1 != null && __0.instanceId > 0)
                affectedGrids.GetValue(__1, _ => Affected);
        }

        private static void BeforePreview(VoxelRaycastHit ___hitBuild, ref BrickInstanceData ___brickData,
            ref Vector3i ___gridPositionInitial, ref Quaternion ___q, out PlacementCheck? __state)
        {
            __state = check;
            if (TryResolveSeam(___hitBuild, ActionBuild.BrickCoord, out var seam, out var orientation))
            {
                ___brickData = seam;
                ___gridPositionInitial = seam.gridPosition;
                ___q = orientation;
            }
            check = Prepare(ActionBuild.BrickCoord);
        }

        private static bool TryResolveSeam(VoxelRaycastHit hit, BrickCoord original, out BrickInstanceData seam, out Quaternion orientation)
        {
            seam = default;
            orientation = default;
            var data = original.brickData;
            if (!IsFitting(data.brickId) || !hit.isHitBrick || !IsStockCargo(hit.brickData.brickId)
                || data.gridLevel != hit.brickData.gridLevel || ActionBuild.PlacementSubGrid.Value != 0
                || Prepare(original) is not { Authorized: true } originalPlacement
                || !ReferenceEquals(hit.Entity, originalPlacement.Entity) || !IsLive(originalPlacement.Entity, hit.brickData))
                return false;

            var exposedNormal = VoxelRotation.ToClosestAxis(hit.localNormal);
            if (Vector3.Dot(exposedNormal, hit.localNormal) < 0.99f) return false;
            var bounds = hit.brickData.GetAabb(useGeoBounds: true);
            var point = hit.localPoint - 0.5f * data.GridSize * exposedNormal;
            float distance = data.GridSize * 0.18f;
            var entity = originalPlacement.Entity;
            bool found = false;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(exposedNormal[axis]) > 0.99f) continue;
                for (int side = -1; side <= 1; side += 2)
                {
                    float edge = side < 0 ? bounds.Min[axis] : bounds.Max[axis];
                    if (Mathf.Abs(hit.localPoint[axis] - edge) > distance) continue;
                    var face = hit;
                    face.localPoint = point;
                    face.localPoint[axis] = edge;
                    face.localNormal = Vector3.zero;
                    face.localNormal[axis] = side;
                    var rotation = Quaternion.FromToRotation(Vector3.up, face.localNormal);
                    var candidate = data;
                    candidate.rotation = rotation.ToByte();
                    candidate.gridPosition = BrickCommon.GetGridPosition(data.Brick, face, rotation, data.gridLevel, 0, globalGridOrigin: false);
                    var placement = new PlacementCheck(candidate, entity, fitting: true, authorized: true);
                    CollectTouchingCargo(placement);
                    if (!placement.HasBothEndpoints || placement.Failed) continue;
                    if (found) return false;
                    seam = candidate;
                    orientation = rotation;
                    found = true;
                }
            }
            return found;
        }

        private static void AfterPreview(ref bool __result)
        {
            if (check != null && Reject(check, __result, mirrored: false)) __result = true;
        }

        private static Exception? FinishPreview(PlacementCheck? __state, Exception? __exception)
        {
            check = __state;
            return __exception;
        }

        private static bool ShouldApply(ref VoxelActionData action, bool mirrored)
        {
            if (action.type != VoxelActionType.build) return true;
            var proposed = action.brickCoord;
            DeferredBuild? deferred = null;
            if (PlayerState.IsSurvival)
            {
                if (mirrored) return !IsFitting(proposed.brickData.brickId);
                int itemId = action.itemDataId;
                int index = deferredBuilds.FindIndex(build => build.Matches(proposed, itemId));
                if (index >= 0)
                {
                    deferred = deferredBuilds[index];
                    deferredBuilds.RemoveAt(index);
                }
                else return !IsFitting(proposed.brickData.brickId);
            }
            var placement = Prepare(proposed);
            if (placement == null) return deferred == null;
            if (!CanApply(placement, proposed, mirrored)) return false;
            if (deferred == null) return true;
            if (!HasBuildItem(proposed.brickData, deferred.ItemId)) return false;
            payingForBuild = true;
            try
            {
                return ActionBuild.ConsumeQuantityForBuild(proposed.brickData, deferred.ItemId);
            }
            finally
            {
                payingForBuild = false;
            }
        }

        private static bool CanApply(PlacementCheck placement, BrickCoord proposed, bool mirrored)
        {
            if (!placement.Authorized)
            {
                if (reportPlacement) Debug.LogWarning($"KEEL_FLUSH_PLACEMENT denied before query fitting={placement.Fitting} main={GameloopManager.IsMainThread} unsafe={GameloopManager.IsNativeContainerAccessUnsafe}");
                return false;
            }

            var previous = check;
            check = placement;
            try
            {
                var overlaps = VoxelPhysics.IsOverlapping(proposed, out _, fastMode: false);
                bool rejected = Reject(placement, overlaps, mirrored);
                if (reportPlacement) placement.Report(overlaps, mirrored, rejected);
                return !rejected;
            }
            catch (Exception exception)
            {
                if (reportPlacement) Debug.LogWarning("KEEL_FLUSH_PLACEMENT query failed: " + exception.GetBaseException().Message);
                return false;
            }
            finally
            {
                check = previous;
            }
        }

        private static bool Reject(PlacementCheck placement, bool overlaps, bool mirrored)
        {
            if (placement.Authorized && placement.Fitting && !overlaps && !mirrored && !placement.Failed)
            {
                try
                {
                    // Flush faces can touch without entering the native collision collector.
                    CollectTouchingCargo(placement);
                }
                catch
                {
                    placement.Failed = true;
                }
            }
            return placement.Reject(overlaps, mirrored);
        }

        private static PlacementCheck? Prepare(BrickCoord proposed)
        {
            bool fitting = IsFitting(proposed.brickData.brickId);
            if (!fitting && (!IsStockCargo(proposed.brickData.brickId) || !FunctionalCommandState.IsSolo))
                return null;

            var entity = proposed.entity;
            if (!fitting && (entity == null || !affectedGrids.TryGetValue(entity, out _)))
                return null;

            bool authorized = enabled && FunctionalCommandState.IsSolo
                && GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
                && entity != null && entity.Parent != null && entity.Parent.IsInitialized
                && !entity.Parent.isDestroyed && !entity.Parent.WillBeUnspawned
                && entity.buildPermission != BuildPermission.cannotBeBuilt && proposed.brickData.Brick.CanBuildOnEntity(entity);
            return new PlacementCheck(proposed.brickData, entity!, fitting, authorized);
        }

        private static bool ScopedOverlap(ref BrickProperties proposedProperties, BrickProperties otherProperties,
            int proposedTransform, int otherTransform, ref SkyPhysics.QueryOverlapAabb query, BrickColliderBVH.Element element)
        {
            var ordinary = proposedProperties.IsOverlapValid(otherProperties, proposedTransform, otherTransform);
            var placement = check;
            if (reportPlacement && placement != null) placement.CollectorCalls++;
            if (placement == null || !placement.Authorized || query.fastMode || query.queryType != SkyPhysics.QueryOverlapType.OverlapBrick
                || query.ourBD != placement.Proposed || query.ourBD.gridLevel != placement.Proposed.gridLevel
                || query.ourBD.instanceId != placement.Proposed.instanceId || proposedTransform != placement.Entity.transformId)
                return ordinary;

            var other = element.brickData;
            if (placement.Fitting ? !IsStockCargo(other.brickId) : !IsFitting(other.brickId))
                return ordinary;
            if (reportPlacement) placement.EndpointCandidates++;
            if (!placement.Fitting) placement.EncounteredFitting = true;
            if (otherTransform != proposedTransform || !IsLive(placement.Entity, other)) return ordinary;

            try
            {
                var props = proposedProperties;
                if (placement.Fitting)
                {
                    if (!IsLiveCargo(placement.Entity, other)
                        || !TryGetFacePort(placement.Proposed, other, out int port)
                        || !placement.AllowEndpoint(port, other.instanceId))
                        return ordinary;
                    props.isTankJoin = true;
                    otherProperties.hasTank = true;
                }
                else
                {
                    if (!TryGetFacePort(other, placement.Proposed, out int port)
                        || !HasOppositeCargo(placement.Entity, other, 1 - port))
                        return ordinary;
                    if (!placement.AllowEndpoint(0, other.instanceId)) return ordinary;
                    props.hasTank = true;
                    otherProperties.isTankJoin = true;
                }
                return props.IsOverlapValid(otherProperties, proposedTransform, otherTransform);
            }
            catch (Exception)
            {
                placement.Failed = true;
                return ordinary;
            }
        }

        private static bool IsFitting(int id)
            => enabled && Fittings.TryGetValue(id, out var fitting) && (uint)id < Brick.BRICK_INDEX.Length
                && ReferenceEquals(Brick.BRICK_INDEX[id], fitting);

        private static bool IsStockCargo(int id)
            => enabled && Fittings.Count > 0 && (uint)id < Brick.BRICK_INDEX.Length
                && ReferenceEquals(Brick.BRICK_INDEX[id], TabStorage.STORAGE_UNIT);

        private static bool IsLive(VoxelEntity entity, BrickInstanceData value)
        {
            if (value.instanceId <= 0 || entity.Bricks == null || value.instanceId >= entity.Bricks.brickIds.Length)
                return false;
            var live = entity.Bricks.GetBrickData(value.instanceId);
            return live.IsValid && live.HealthFactor > 0f && live == value
                && live.instanceId == value.instanceId && live.gridLevel == value.gridLevel;
        }

        private static bool IsLiveCargo(VoxelEntity entity, BrickInstanceData value)
        {
            if (new BrickCoord(entity.ID, value).Command is not StorageCommandData cargo || !cargo.isCargo
                || cargo.StorageId <= 0) return false;
            var context = StorageCommon.CONTEXT;
            if (cargo.StorageId >= context.StorageLength) return false;
            var storage = context.GetStorageData(cargo.StorageId);
            return storage.id == cargo.StorageId && storage.type == cargo.ItemStorageType
                && storage.brickInstanceId == value.instanceId && storage.childrenEntityId == entity.ChildrenID
                && storage.parentEntityId == entity.Parent.IndexID;
        }

        private static bool HasOppositeCargo(VoxelEntity entity, BrickInstanceData fitting, int opposite)
        {
            var query = CargoSearch(entity, fitting);
            try
            {
                query.Execute();
                int found = 0;
                for (int index = 0; index < query.bricksElements.Length; index++)
                {
                    var candidate = query.bricksElements[index].brickData;
                    if (!TryGetCargoPort(entity, fitting, candidate, out int port) || port != opposite)
                        continue;
                    if (found != 0 && found != candidate.instanceId) return false;
                    found = candidate.instanceId;
                }
                return found != 0;
            }
            finally
            {
                if (query.bricksElements.IsCreated) query.bricksElements.Dispose();
            }
        }

        private static void CollectTouchingCargo(PlacementCheck placement)
        {
            var query = CargoSearch(placement.Entity, placement.Proposed);
            try
            {
                query.Execute();
                for (int index = 0; index < query.bricksElements.Length; index++)
                {
                    var candidate = query.bricksElements[index].brickData;
                    if (TryGetCargoPort(placement.Entity, placement.Proposed, candidate, out int port)
                        && !placement.AllowEndpoint(port, candidate.instanceId))
                        return;
                }
            }
            finally
            {
                if (query.bricksElements.IsCreated) query.bricksElements.Dispose();
            }
        }

        private static bool TryGetCargoPort(VoxelEntity entity, BrickInstanceData fitting, BrickInstanceData candidate, out int port)
        {
            port = -1;
            return IsStockCargo(candidate.brickId) && IsLive(entity, candidate) && IsLiveCargo(entity, candidate)
                && TryGetFacePort(fitting, candidate, out port);
        }

        private static SkyPhysics.QueryOverlapBoundsOnEntity CargoSearch(VoxelEntity entity, BrickInstanceData fitting)
        {
            var bounds = fitting.GetAabb(useGeoBounds: false);
            var padding = new float3(0.1f);
            bounds.Min -= padding;
            bounds.Max += padding;
            return new SkyPhysics.QueryOverlapBoundsOnEntity(entity.brickTree.id, bounds, Allocator.Temp, SkyPhysics.CONTEXT);
        }

        private static bool TryGetFacePort(BrickInstanceData fitting, BrickInstanceData cargo, out int index)
        {
            index = -1;
            if (fitting.gridLevel != cargo.gridLevel) return false;
            var ports = StorageAttachmentPoint.GetPoints(fitting.brickId, ItemStorageType.tube);
            if (ports.Count != 2) return false;
            var bounds = cargo.GetAabb(useGeoBounds: true);
            for (var port = 0; port < ports.Count; port++)
            {
                var point = ports[port].Transform(fitting);
                if (!FlushPortGeometry.IsAligned(point.point, point.normal, bounds.Min, bounds.Max))
                    continue;
                if (index >= 0) return false;
                index = port;
            }
            return index >= 0;
        }

        private sealed class PlacementCheck
        {
            internal BrickInstanceData Proposed { get; }
            internal VoxelEntity Entity { get; }
            internal bool Fitting { get; }
            internal bool Authorized { get; }
            internal bool EncounteredFitting { get; set; }
            internal bool Failed { get; set; }
            internal int CollectorCalls { get; set; }
            internal int EndpointCandidates { get; set; }
            private readonly int[] endpoints = new int[2];
            internal bool HasBothEndpoints => endpoints[0] != 0 && endpoints[1] != 0;

            internal PlacementCheck(BrickInstanceData proposed, VoxelEntity entity, bool fitting, bool authorized)
            {
                Proposed = proposed;
                Entity = entity;
                Fitting = fitting;
                Authorized = authorized;
            }

            internal bool AllowEndpoint(int port, int id)
            {
                if ((endpoints[port] != 0 && endpoints[port] != id) || endpoints[1 - port] == id)
                {
                    Failed = true;
                    return false;
                }
                endpoints[port] = id;
                return true;
            }

            internal void Report(bool overlaps, bool mirrored, bool rejected)
            {
                Debug.LogWarning($"KEEL_FLUSH_PLACEMENT fitting={Fitting} grid={Proposed.gridPosition} collector={CollectorCalls} candidates={EndpointCandidates} ports={endpoints[0]}/{endpoints[1]} overlaps={overlaps} mirrored={mirrored} failed={Failed} rejected={rejected}");
            }

            internal bool Reject(bool overlaps, bool mirrored)
            {
                if (!Authorized || Failed) return true;
                if (Fitting) return mirrored || overlaps || (endpoints[0] == 0 && endpoints[1] == 0);
                return overlaps || (EncounteredFitting && mirrored);
            }
        }
    }
}
