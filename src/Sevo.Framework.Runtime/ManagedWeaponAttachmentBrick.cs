using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using Unity.Collections;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal sealed class ManagedWeaponAttachmentBrick : AddonBrick
    {
        private const float NativeLookupRadius = 0.1f;
        [ThreadStatic] private static BrickCoord? constructing;

        internal ManagedWeaponAttachmentBrick(int id, string displayName, string mesh, ResizeMode mode, object[] recipe, string key)
            : base(id, displayName, mesh, mode, false, AddonCommand.Type.weaponAddon, recipe)
        {
            Key = key;
            forceLocalGrid = true;
            NoMirroring = true;
        }

        internal string Key { get; }

        public override bool CanBuild(VoxelRaycastHit hit, Vector3i gridPosition, int gridScale, ref Quaternion rotation) =>
            WeaponAttachmentAvailability.Enabled && base.CanBuild(hit, gridPosition, gridScale, ref rotation);

        public override BrickIds OnConstruct(BrickCoord brickCoord, bool onLoad, Dictionary<int, byte[]> additionalDatas)
        {
            if (onLoad)
                return base.OnConstruct(brickCoord, onLoad, additionalDatas);
            var previous = constructing;
            constructing = brickCoord;
            try
            {
                return base.OnConstruct(brickCoord, onLoad, additionalDatas);
            }
            finally
            {
                constructing = previous;
            }
        }

        internal static void InstallCoreHooks(Harmony harmony, Assembly game) => InstallHooks(harmony, game, core: true);
        internal static void InstallInteractionHooks(Harmony harmony, Assembly game) => InstallHooks(harmony, game, core: false);

        private static void InstallHooks(Harmony harmony, Assembly game, bool core)
        {
            const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            MethodInfo Target(string type, string name, params Type[] parameters) =>
                game.GetType(type, false)?.GetMethod(name, instance, null, parameters, null)
                ?? throw new InvalidOperationException("Missing live attachment hook: " + type + "." + name);
            MethodInfo Hook(string name) => typeof(ManagedWeaponAttachmentBrick).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

            var hooks = core ? new[]
            {
                (Target("AddonBrick", "GetCommand", typeof(BrickCoord), typeof(Vector3i)), Hook(nameof(FilterLookup)), HarmonyPatchType.Postfix),
                (Target("AddonBrick", "OnConstruct", typeof(BrickCoord), typeof(bool), typeof(Dictionary<int, byte[]>)),
                    Hook(nameof(ResolveNativeConstructionReceiver)), HarmonyPatchType.Transpiler),
                (Target("VoxelEngine.AddonCommand", "Link", typeof(AddonBrick), typeof(BrickCoord), typeof(bool)), Hook(nameof(AllowLink)), HarmonyPatchType.Prefix),
                (Target("VoxelEngine.EntityBricks", "Add", typeof(BrickInstanceData)), Hook(nameof(AllowAllocation)), HarmonyPatchType.Prefix)
            } : new[]
            {
                (Target("VoxelEngine.ActionBuild", "CanBuild"), Hook(nameof(AlignPreview)), HarmonyPatchType.Prefix),
                (Target("NetworkedBuilder", "DoAction", typeof(VoxelActionData).MakeByRefType(), typeof(bool), typeof(int)),
                    Hook(nameof(AllowBuildAction)), HarmonyPatchType.Prefix)
            };
            var installed = new List<(MethodInfo Original, MethodInfo Patch)>();
            try
            {
                foreach (var (original, patch, kind) in hooks)
                {
                    try
                    {
                        var hook = new HarmonyMethod(patch);
                        harmony.Patch(original,
                            prefix: kind == HarmonyPatchType.Prefix ? hook : null,
                            postfix: kind == HarmonyPatchType.Postfix ? hook : null,
                            transpiler: kind == HarmonyPatchType.Transpiler ? hook : null);
                        installed.Add((original, patch));
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException(
                            $"Attachment {kind} on {original.DeclaringType?.FullName}.{original.Name} failed: {error}", error);
                    }
                }
            }
            catch (Exception error)
            {
                var rollbackErrors = new List<string>();
                installed.Reverse();
                foreach (var (original, patch) in installed)
                {
                    try { harmony.Unpatch(original, patch); }
                    catch (Exception rollback) { rollbackErrors.Add($"{original.DeclaringType?.FullName}.{original.Name}: {rollback}"); }
                }
                if (rollbackErrors.Count != 0)
                    throw new InvalidOperationException(error.Message + Environment.NewLine
                        + "Attachment rollback failed: " + string.Join(Environment.NewLine, rollbackErrors), error);
                throw;
            }
        }

        private static bool AlignPreview(Brick ___brick, VoxelRaycastHit ___hitBuild, Vector3i ___gridPositionInitial,
            ref Quaternion ___q, ref BrickInstanceData ___brickData, ref bool __result)
        {
            if (___brick is not ManagedWeaponAttachmentBrick attachment)
                return true;
            if (___hitBuild.isHitBrick)
            {
                var hit = ___hitBuild.brickCoord;
                var candidate = new BrickCoord(hit.entityID, ___brickData);
                var receiver = ReceiverAt(hit);
                var slot = FindSlot(receiver, attachment, candidate, ___gridPositionInitial, matchRotation: false);
                if (slot != null)
                {
                    ___q = slot.brickCoord.quaternion;
                    ___brickData.rotation = slot.brickData.rotation;
                    return true;
                }
            }
            __result = false;
            return false;
        }

        private static bool AllowBuildAction(ref VoxelActionData __0, bool __1)
        {
            if (__0.type != VoxelActionType.build || ManagedDefinition(__0.brickData) is not { } attachment)
                return true;
            return !__1 && ValidPlacement(attachment, __0.brickCoord);
        }

        private static bool AllowAllocation(EntityBricks __instance, BrickInstanceData __0, ref int __result)
        {
            if (ManagedDefinition(__0) is not { } attachment)
                return true;
            if (__instance.entity != null && ValidPlacement(attachment, new BrickCoord(__instance.entity.ID, __0)))
                return true;
            __result = 0;
            return false;
        }

        private static ManagedWeaponAttachmentBrick? ManagedDefinition(BrickInstanceData data)
        {
            var definitions = Brick.BRICK_INDEX;
            return data.brickId < definitions.Length ? definitions[data.brickId] as ManagedWeaponAttachmentBrick : null;
        }

        private static bool ValidPlacement(ManagedWeaponAttachmentBrick attachment, BrickCoord candidate)
        {
            var entity = candidate.entity;
            if (entity == null || entity.WillBeUnspawned || candidate.brickData.scale != 0
                || entity.buildPermission is BuildPermission.cannotBeBuilt or BuildPermission.onlyDecals)
                return false;
            var parent = entity.Parent;
            if (parent == null || parent.isDestroyed || !attachment.CanBuildOnEntity(entity))
                return false;
            var query = new SkyPhysics.QueryOverlapSphereOnEntity(
                entity.brickTree.id, candidate.Position, NativeLookupRadius, Allocator.Temp, SkyPhysics.CONTEXT);
            AddonCommand? match = null;
            try
            {
                var bricks = query.Execute();
                foreach (var item in bricks)
                {
                    var hit = new BrickCoord(candidate.entityID, item.brickData);
                    var slot = FindSlot(ReceiverAt(hit), attachment, candidate, candidate.brickData.gridPosition, matchRotation: true);
                    if (slot == null)
                        continue;
                    if (match != null && !ReferenceEquals(match, slot))
                        return false;
                    match = slot;
                }
            }
            finally
            {
                if (query.brickElements.IsCreated)
                    query.brickElements.Dispose();
            }
            // EntityBricks.Add assigns a fresh ID. Collision checks must not treat an incoming copied ID as the candidate's identity.
            candidate.brickData.instanceId = 0;
            return match != null && !VoxelPhysics.IsOverlapping(candidate, false);
        }

        private static void FilterLookup(AddonBrick __instance, BrickCoord __0, Vector3i __1, ref AddonCommand? __result)
        {
            if (__instance is not ManagedWeaponAttachmentBrick attachment)
            {
                if (__result?.receiver is WeaponAttachmentReceiverCommand)
                    __result = null;
                return;
            }

            var receiver = ReceiverAt(__0);
            var candidate = constructing ?? __0;
            var match = FindSlot(receiver, attachment, candidate, __1, matchRotation: constructing.HasValue,
                checkGrid: constructing.HasValue);
            // A hit on a verified repeat has no virtual command in ordinary preview.
            __result = __result == null || ReferenceEquals(__result, match) ? match : null;
        }

        private static IEnumerable<CodeInstruction> ResolveNativeConstructionReceiver(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(BrickCoord), nameof(BrickCoord.Command));
            var replacement = AccessTools.Method(typeof(ManagedWeaponAttachmentBrick), nameof(ConstructionReceiver));
            var body = new List<CodeInstruction>(instructions);
            var sites = body.FindAll(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, getter));
            if (sites.Count != 1 || replacement == null)
                throw new InvalidOperationException("Native attachment construction receiver lookup changed.");
            sites[0].operand = replacement;
            return body;
        }

        private static ICommand? ConstructionReceiver(ref BrickCoord hit)
        {
            var native = hit.Command;
            if (native != null || constructing is not { } candidate || ManagedDefinition(candidate.brickData) is not { } attachment)
                return native;
            var receiver = RepeatReceiverAt(hit);
            return FindSlot(receiver, attachment, candidate, candidate.brickData.gridPosition, matchRotation: true) != null
                ? receiver : null;
        }

        private static bool AllowLink(AddonCommand __instance, AddonBrick __0, BrickCoord __1, bool __2)
        {
            if (__0 is not ManagedWeaponAttachmentBrick attachment)
                return __instance.receiver is not WeaponAttachmentReceiverCommand;
            var entity = __1.entity;
            return entity != null && ReferenceEquals(ManagedDefinition(__1.brickData), attachment) && __1.brickData.IsValid
                && SamePlacement(__1.brickData, entity.Bricks.GetBrickData(__1.brickData.instanceId))
                // Reload must not let a second receiver claim the same physical add-on.
                && (!__2 || entity.Bricks.GetBrickIds(__1.brickData.instanceId).commandId<=0)
                && __1.brickData.scale == 0 && __instance.receiver is WeaponAttachmentReceiverCommand receiver
                && Matches(__instance, __1, __1.brickData.gridPosition, matchRotation: true, checkGrid: true)
                && receiver.CanLinkAttachment(__instance, attachment);
        }

        private static WeaponAttachmentReceiverCommand? ReceiverAt(BrickCoord hit)
        {
            if (!hit.brickData.IsValid || hit.entity == null)
                return null;
            if (hit.Command is WeaponAttachmentReceiverCommand receiver
                && hit.entityID == receiver.brickCoord.entityID && SamePlacement(hit.brickData, receiver.brickData))
                return receiver;
            return RepeatReceiverAt(hit);
        }

        private static WeaponAttachmentReceiverCommand? RepeatReceiverAt(BrickCoord hit)
        {
            var data = hit.brickData;
            var entity = hit.entity;
            if (!data.IsValid || data.Brick?.isRepeatBrick != true || entity == null
                || !SamePlacement(data, entity.Bricks.GetBrickData(data.instanceId))
                || CommandManager.GetCommand(hit.BrickIds.commandId) is not WeaponAttachmentReceiverCommand receiver)
                return null;
            return receiver.brickCoord.entityID==hit.entityID && MatchesRepeatPlacement(receiver.brickData,data) ? receiver : null;
        }

        internal static bool MatchesRepeatPlacement(BrickInstanceData parent,BrickInstanceData data)
        {
            var definition = parent.Brick;
            if (definition?.VoxelMesh == null || !FunctionalStockTemplate.IsReceiverRepeat(definition,data.brickId)
                || parent.rotation != data.rotation || parent.gridLevel != data.gridLevel)
                return false;
            var local = parent.QuaternionInv * (data.gridPosition - parent.gridPosition);
            int unit = Mathf.RoundToInt(parent.GridSize * definition.VoxelMesh.ResizeMode.unitStud);
            if (local.x != 0 || local.z != 0 || unit <= 0 || local.y % unit != 0)
                return false;
            int index = local.y / unit;
            return index >= definition.repeatBrickStart && index <= parent.Scale.y + definition.repeatBrickEndOffset;
        }

        private static bool SamePlacement(BrickInstanceData first, BrickInstanceData second) =>
            first.instanceId == second.instanceId && first.brickId == second.brickId
            && first.gridPosition == second.gridPosition && first.rotation == second.rotation
            && first.gridLevel == second.gridLevel && first.scale == second.scale;

        private static AddonCommand? FindSlot(WeaponAttachmentReceiverCommand? receiver, ManagedWeaponAttachmentBrick attachment,
            BrickCoord candidate, Vector3i position, bool matchRotation, bool checkGrid = true)
        {
            if (receiver == null)
                return null;
            AddonCommand? match = null;
            foreach (var slot in receiver.addonCommands)
            {
                if (slot == null || !Matches(slot, candidate, position, matchRotation, checkGrid)
                    || !receiver.CanLinkAttachment(slot, attachment))
                    continue;
                if (match != null)
                    return null;
                match = slot;
            }
            return match;
        }

        private static bool Matches(AddonCommand slot, BrickCoord candidate, Vector3i position, bool matchRotation, bool checkGrid)
        {
            var slotData = slot.brickData;
            return slot.brickCoord.entityID == candidate.entityID && slotData.gridPosition == position
                && (!checkGrid || slotData.gridLevel == candidate.brickData.gridLevel)
                && (!matchRotation || slotData.rotation == candidate.brickData.rotation);
        }
    }
}
