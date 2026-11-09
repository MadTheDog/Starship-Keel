using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Internal creative prototype for a stock-faced flush storage fitting.</summary>
    internal static class FlushStoragePlacement
    {
        private static readonly Dictionary<int, BrickPipe> Fittings = new Dictionary<int, BrickPipe>();
        private static readonly FieldInfo LabelsField = typeof(CodeInstruction).GetField("labels")!;
        private static readonly object Affected = new object();
        private static ConditionalWeakTable<VoxelEntity, object> affectedGrids = new ConditionalWeakTable<VoxelEntity, object>();
        [ThreadStatic] private static PlacementCheck? check;
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
            var query = typeof(SkyPhysics.QueryOverlapAabb);
            var collector = AccessTools.Method(query, nameof(SkyPhysics.QueryOverlapAabb.OnElementIntersect),
                new[] { typeof(BrickColliderBVH.Element), typeof(int), typeof(bool), typeof(int) });
            var builder = AccessTools.TypeByName("NetworkedBuilder");
            var rpc = AccessTools.Method(builder, "UserCode_RpcVoxelAction", new[] { typeof(byte[]), typeof(bool), typeof(int) });
            var preview = AccessTools.Method(typeof(ActionBuild), "IsColliding", Type.EmptyTypes);
            harmony.Patch(collector, transpiler: Hook(nameof(PatchCollector)));
            harmony.Patch(rpc, transpiler: Hook(nameof(PatchCommit)));
            harmony.Patch(preview, prefix: Hook(nameof(BeforePreview)), postfix: Hook(nameof(AfterPreview)),
                finalizer: Hook(nameof(FinishPreview)));
            harmony.Patch(AccessTools.Method(typeof(BrickPipe), nameof(BrickPipe.OnEntityInitialized),
                new[] { typeof(BrickInstanceData), typeof(VoxelEntity), typeof(bool) }), postfix: Hook(nameof(AfterPipeAdded)));
            reportPlacement = Environment.GetEnvironmentVariable("SEVO_KEEL_FLUSH_STORAGE_PLACEMENT_DIAGNOSTICS") == "1";
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            reportPlacement = false;
            check = null;
            Fittings.Clear();
            affectedGrids = new ConditionalWeakTable<VoxelEntity, object>();
        }

        private static HarmonyMethod Hook(string method) => new HarmonyMethod(typeof(FlushStoragePlacement), method);

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

        private static void BeforePreview(out PlacementCheck? __state)
        {
            __state = check;
            check = Prepare(ActionBuild.BrickCoord);
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
            var placement = Prepare(proposed);
            if (placement == null) return true;
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
            if (!fitting && (!IsStockCargo(proposed.brickData.brickId)
                || !FunctionalCommandState.IsSolo || PlayerState.gameMode != PlayerState.GameMode.creative))
                return null;

            var entity = proposed.entity;
            if (!fitting && (entity == null || !affectedGrids.TryGetValue(entity, out _)))
                return null;

            bool authorized = enabled && FunctionalCommandState.IsSolo && PlayerState.gameMode == PlayerState.GameMode.creative
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
