using System;
using System.Collections.Generic;
using Common;
using HarmonyLib;
using Manager;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal static class MechanismStockRail
    {
        private const int MaxMembers = 64;
        private const int MaxBrickSlots = 4096;
        private const float MaxRadius = 50f;
        private static AccessTools.FieldRef<SliderSystem, AddonCommand>? negative;
        private static AccessTools.FieldRef<SliderSystem, AddonCommand>? positive;
        internal static bool IsAvailable => negative != null && positive != null;

        internal static void Enable()
        {
            var left = AccessTools.FieldRefAccess<SliderSystem, AddonCommand>("addonNeg");
            var right = AccessTools.FieldRefAccess<SliderSystem, AddonCommand>("addonPos");
            negative = left;
            positive = right;
        }

        internal static void Disable() => negative = positive = null;

        private static bool CanRead => IsAvailable && FunctionalCommandState.IsSolo
            && GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe;

        internal static bool TryDiscover(SliderSystem? slider, out MechanismPath? path)
        {
            path = null;
            if (!CanRead || slider == null || slider.IsRemoved) return false;
            var entity = slider.brickCoord.entity;
            if (entity == null || !entity.IsInitialized || entity.WillBeUnspawned) return false;
            var stock = slider.brickCoord.brickData;
            var left = negative!(slider);
            var right = positive!(slider);
            if (!ReferenceEquals(stock.Brick, TabMechanism.SLIDING) || left == null || right == null
                || !float.IsFinite(stock.GridSize) || stock.GridSize <= 0f
                || !CheckStraightChain(slider, left, -1, out int leftUnits)
                || !CheckStraightChain(slider, right, 1, out int rightUnits))
                return false;
            float length = stock.GridSize * (stock.Scale.x + leftUnits + rightUnits);
            if (!float.IsFinite(length) || length < 0.001f || length != slider.amplitude) return false;
            path = MechanismPath.FromConnectedRail(new[] { System.Numerics.Vector3.Zero,
                System.Numerics.Vector3.UnitX * length });
            return true;
        }

        private static bool CheckStraightChain(SliderSystem slider, AddonCommand root, int side, out int units)
        {
            units = 0;
            var entity = slider.brickCoord.entity;
            var stock = slider.brickCoord.brickData;
            var direction = side * stock.LocalRight;
            int halfGrid = Mathf.RoundToInt(16f * stock.GridSize);
            var expected = stock.gridPosition + stock.Quaternion * new Vector3i(
                halfGrid * (side < 0 ? -1 : 2 * stock.Scale.x + 1), halfGrid, 0);
            IAddonReceiverSystem receiver = slider;
            AddonCommand? command = root;
            for (int piece = 0; command != null && piece < MaxMembers; piece++)
            {
                var socket = command.brickCoord.brickData;
                if (command.IsRemoved || !ReferenceEquals(command.receiver, receiver)
                    || !ReferenceEquals(command.brickCoord.entity, entity) || command.addonType != AddonCommand.Type.slider
                    || socket.gridLevel != stock.gridLevel || socket.gridPosition != expected
                    || (socket.LocalUp - direction).sqrMagnitude > 0.000001f)
                    return false;
                var addon = command.addon_bd;
                if (!addon.IsValid)
                    return command.totalStack == 0 && units == root.totalStack && IsEmptySocket(entity, expected);
                var current = entity.Bricks.GetBrickData(addon.instanceId);
                if (!current.IsValid || current != addon || !ReferenceEquals(current.Brick, TabMechanism.SLIDING_ADDON)
                    || current.gridPosition != socket.gridPosition || current.rotation != socket.rotation
                    || current.gridLevel != stock.gridLevel || current.Scale.x != 0 || current.Scale.z != 0
                    || current.healthScore == 0)
                    return false;
                units += current.Count();
                expected += current.Quaternion * new Vector3i(0, current.UnitGrid * (current.Scale.y + 1), 0);
                receiver = command;
                command = command.stackAddonCommand;
            }
            return false;
        }

        private static bool IsEmptySocket(VoxelEntity entity, Vector3i position)
        {
            // Native relinking is asynchronous on load. Do not mistake a waiting extension for an empty socket.
            var data = entity.Bricks.brickDatas;
            if (!data.IsCreated || data.Length > MaxBrickSlots) return false;
            for (int index = 1; index < data.Length; index++)
            {
                var brick = data[index];
                if (brick.IsValid && brick.gridPosition == position && ReferenceEquals(brick.Brick, TabMechanism.SLIDING_ADDON))
                    return false;
            }
            return true;
        }

        internal static bool CanSweep(MechanismRailSweep sweep)
        {
            if (!CanRead) return false;
            var parent = sweep.Slider.Placement.entity;
            if (parent == null || parent.WillBeUnspawned || !parent.IsInitialized) return false;
            var context = SkyPhysics.CONTEXT;
            if (!context.transformDatas.IsCreated || context.WorldTreeRootId <= 0
                || !TryReadPose(context, parent.transformId, out var parentPose)) return false;
            var local = sweep.ToOffset - sweep.FromOffset;
            var delta = parentPose.scale * (parentPose.rotation * (sweep.Slider.Placement.quaternion
                * new Vector3(local.X, local.Y, local.Z)));
            if (!Finite(delta) || delta.sqrMagnitude > 0.051f * 0.051f) return false;

            var moving = new HashSet<int>();
            var members = new List<(VoxelEntity Entity, SkyTransformData Pose)>();
            if (!Collect(sweep.Carriage, parent, parentPose, context, moving, members)) return false;
            int remainingSlots = MaxBrickSlots;
            foreach (var member in members)
            {
                var data = member.Entity.Bricks.brickDatas;
                if (!data.IsCreated || data.Length > remainingSlots) return false;
                remainingSlots -= data.Length;
                var bounds = Aabb.Empty;
                bool solid = false;
                for (int index = 1; index < data.Length; index++)
                {
                    var brick = data[index];
                    if (!brick.IsValid || brick.healthScore == 0) continue;
                    var definition = brick.Brick;
                    if (definition == null) return false;
                    if (definition.IsCollisionOff || definition.IsDecal) continue;
                    var brickBounds = brick.GetAabb(useGeoBounds: false);
                    if (!Valid(brickBounds)) return false;
                    bounds = solid ? Aabb.Union(bounds, brickBounds) : brickBounds;
                    solid = true;
                }
                if (solid && !SweepBounds(context, member.Pose, delta, bounds, moving)) return false;
            }
            return true;
        }

        private static bool Collect(ChildrenEntity entity, VoxelEntity parent, SkyTransformData parentPose,
            SkyPhysics.QueryContext context, HashSet<int> moving, List<(VoxelEntity, SkyTransformData)> members)
        {
            if (entity == null || entity.WillBeUnspawned || !entity.IsInitialized
                || !ReferenceEquals(entity.DirectParent, parent) || entity.directDockedChildrens.Count != 0
                || members.Count >= MaxMembers || !moving.Add(entity.transformId)
                || !TryReadPose(context, entity.transformId, out var row) || row.parentId != parent.transformId
                || row.localScale != 1f || !Finite(row.localPosition)) return false;
            var pose = parentPose.Transform(row.localPosition, row.localRotation, row.localScale);
            if (!ValidRotation(pose.rotation)) return false;
            members.Add((entity, pose));
            foreach (var child in entity.DirectChildrens)
                if (!Collect(child, entity, pose, context, moving, members)) return false;
            return true;
        }

        private static bool SweepBounds(SkyPhysics.QueryContext context, SkyTransformData pose, Vector3 delta,
            Aabb bounds, HashSet<int> moving)
        {
            var offset = pose.InverseTransformDirection(delta) / pose.scale;
            var end = new Aabb { Min = bounds.Min + (float3)offset, Max = bounds.Max + (float3)offset };
            var swept = Aabb.Union(bounds, end);
            float radius = swept.Radius + 0.005f;
            if (!Valid(swept) || !float.IsFinite(radius) || radius > MaxRadius) return false;
            var candidates = new SkyPhysics.QueryOverlapCapsuleCandidates(pose.LocalToSystem(swept.CenterV3),
                radius, Allocator.Temp, context, -1);
            try
            {
                candidates.Execute();
                if (candidates.candidateSkyColliders.Length != 0 || candidates.candidateWorldItems.Length != 0)
                    return false;
                foreach (var brick in candidates.candidateBricks)
                {
                    if (moving.Contains(brick.entityTransformId)) continue;
                    if (!Clear(swept, pose, brick.element.bounds, context, brick.entityTransformId, includeTouch: false))
                        return false;
                }
                foreach (var triangle in candidates.candidateTriangles)
                    if (!Clear(swept, pose, triangle.element.bounds, context, triangle.worldTransformId, includeTouch: true))
                        return false;
                return true;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private static bool Clear(Aabb swept, SkyTransformData pose, Aabb obstacle, SkyPhysics.QueryContext context,
            int transformId, bool includeTouch)
        {
            if (!Valid(obstacle) || !TryReadPose(context, transformId, out var otherPose)) return false;
            var target = new Aabb.OverlapHelper(otherPose, pose).ToTargetAABB(obstacle);
            if (!Valid(target)) return false;
            return includeTouch
                ? math.any(swept.Max < target.Min | target.Max < swept.Min)
                : math.any(swept.Max <= target.Min | target.Max <= swept.Min);
        }

        private static bool TryReadPose(SkyPhysics.QueryContext context, int id, out SkyTransformData pose)
        {
            pose = default;
            if (id <= 0 || id >= context.transformDatas.Length) return false;
            pose = context.transformDatas[id];
            return pose.scale == 1f && ValidRotation(pose.rotation);
        }

        private static bool ValidRotation(Quaternion rotation)
            => Math.Abs(Quaternion.Dot(rotation, rotation) - 1f) < 0.0001f;

        private static bool Valid(Aabb bounds) => bounds.IsValid && Finite(bounds.Min) && Finite(bounds.Max);

        private static bool Finite(Vector3 vector)
            => float.IsFinite(vector.x) && float.IsFinite(vector.y) && float.IsFinite(vector.z);
    }
}
