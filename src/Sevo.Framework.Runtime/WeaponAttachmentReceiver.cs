using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A snapshot of one native side slot and its current attachment, if present.</summary>
    public readonly struct WeaponAttachmentSlot
    {
        internal WeaponAttachmentSlot(int index, BrickCoord slot, string? attachmentKey, BrickCoord? attachment)
        {
            Index = index;
            Slot = slot;
            AttachmentKey = attachmentKey;
            Attachment = attachment;
        }

        public int Index { get; }
        /// <summary>The native socket coordinate. It has no physical brick instance ID.</summary>
        public BrickCoord Slot { get; }
        public string? AttachmentKey { get; }
        public BrickCoord? Attachment { get; }
    }

    /// <summary>Native receiver constructed by the game. Register through ContentRegistration.</summary>
    internal sealed class WeaponAttachmentReceiverCommand : CommandSystem, IAddonReceiverSystem, IFunctionalCommand
    {
        private FunctionalCommandState FunctionalState { get; }
        FunctionalCommandState IFunctionalCommand.FunctionalState => FunctionalState;

        public WeaponAttachmentReceiverCommand() => FunctionalState = new FunctionalCommandState(this, GetAttachmentSlots);

        public override bool CanBeLinkSource => false;
        public override bool CanBeLinkTarget => true;
        public override bool AllowMultipleInputs => false;
        public override bool CanActivateManually(PlayerCommandManager playerManager) => false;
        public override bool CanBeLinkTargetFromCommand(ICommand predecessor) =>
            FunctionalCommandState.SupportsSource(predecessor);

        protected override void OnInitialize(BrickIds ids, bool onLoad, object[] args)
        {
            FunctionalState.Stop();
            base.OnInitialize(ids, onLoad, args);
            FunctionalState.Initialize(onLoad, args);
            if (!WeaponAttachmentAvailability.Enabled || !KeelFunctionalCommand.Enabled || !FunctionalCommandState.IsSolo ||
                FunctionalState.Binding?.CompatibleAttachmentKeys is not { Count: > 0 }) return;

            var data = brickData;
            var unit = data.UnitGrid;
            var offset = data.Quaternion * new Vector3i(0, unit / 2, unit / 2);
            var rotation = (brickCoord.quaternion * Quaternion.LookRotation(-Vector3.up, Vector3.forward)).ToByte();
            var normal = brickCoord.Normal;
            var length = data.Scale.y;
            for (var index = 0; index <= length; index++)
            {
                var position = data.gridPosition + (index * unit) * normal + offset;
                SpawnNewAddonCommand(position, rotation, vertexDataPointer, pixelDataPointer,
                    this, AddonCommand.Type.weaponAddon, onLoad);
            }
        }

        public override void OnEntityInitialized(bool onLoad)
        {
            base.OnEntityInitialized(onLoad);
            FunctionalState.Start();
        }

        // Attachments contribute geometry and identity. Keel leaves native weapon stats unused.
        public override void OnAddonBlockUpdate(bool onLoad) { }

        // Verified stock source hooks deliver the sustained level through FunctionalState.
        public override void Activate(bool manualActivation, PlayerCommandManager playerManager, float state) { }

        public override void OnPredecessorAdded(ICommand predecessor, bool onLoad, bool onInit)
        {
            base.OnPredecessorAdded(predecessor, onLoad, onInit);
            FunctionalState.PredecessorAdded(predecessor, onLoad, onInit);
        }

        public override void OnPredecessorRemoved(ICommand predecessor)
        {
            base.OnPredecessorRemoved(predecessor);
            FunctionalState.PredecessorRemoved();
        }

        internal bool CanLinkAttachment(AddonCommand slot, ManagedWeaponAttachmentBrick attachment) =>
            IsCurrentSlot(slot) && !slot.addon_bd.IsValid && IsCompatible(attachment);

        private IReadOnlyList<WeaponAttachmentSlot> GetAttachmentSlots()
        {
            if (addonCommands.Count == 0) return Array.Empty<WeaponAttachmentSlot>();

            var snapshot = new WeaponAttachmentSlot[addonCommands.Count];
            for (var index = 0; index < snapshot.Length; index++)
            {
                var slot = addonCommands[index];
                var socket = slot.brickCoord;
                var recorded = slot.addon_bd;
                string? key = null;
                BrickCoord? attachment = null;
                if (recorded.IsValid && IsCurrentSlot(slot) && socket.entity is { } grid)
                {
                    var bricks = grid.Bricks;
                    var current = bricks.GetBrickData(recorded.instanceId);
                    if (current.IsValid && current.scale == 0 && current.instanceId == recorded.instanceId &&
                        current.brickId == recorded.brickId && current.gridPosition == socket.brickData.gridPosition &&
                        current.gridLevel == socket.brickData.gridLevel && current.rotation == socket.brickData.rotation &&
                        current.Brick is ManagedWeaponAttachmentBrick definition && IsCompatible(definition) &&
                        bricks.GetBrickIds(current.instanceId).commandId == slot.commandId)
                    {
                        key = definition.Key;
                        attachment = new BrickCoord(socket.entityID, current);
                    }
                }
                snapshot[index] = new WeaponAttachmentSlot(index, socket, key, attachment);
            }
            return Array.AsReadOnly(snapshot);
        }

        private bool IsCurrentSlot(AddonCommand slot) =>
            FunctionalState.CanRun && commandId > 0 && ReferenceEquals(CommandManager.GetCommand(commandId), this) &&
            slot.commandId > 0 && !slot.IsRemoved && slot.addonType == AddonCommand.Type.weaponAddon &&
            ReferenceEquals(slot.receiver, this) && slot.brickCoord.entityID == brickCoord.entityID &&
            ReferenceEquals(CommandManager.GetCommand(slot.commandId), slot) && addonCommands.Contains(slot);

        private bool IsCompatible(ManagedWeaponAttachmentBrick attachment) =>
            FunctionalState.Binding?.CompatibleAttachmentKeys?.Contains(attachment.Key, StringComparer.Ordinal) == true;

        public override void OnDestroy(BrickUnspawnMode mode)
        {
            try { FunctionalState.Stop(); }
            finally { base.OnDestroy(mode); }
        }

        public override void ReleaseResources()
        {
            try { FunctionalState.Stop(); }
            finally { base.ReleaseResources(); }
        }
    }
}
