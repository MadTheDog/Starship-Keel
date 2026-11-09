using System;
using System.Collections.Generic;
using System.Threading;
using Game;
using Manager;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A named, explicitly selected placement in a block assembly.</summary>
    public readonly struct BlockAssemblyPart
    {
        public BlockAssemblyPart(string name, BrickCoord placement)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Placement = placement;
        }

        public string Name { get; }
        public BrickCoord Placement { get; }
    }

    /// <summary>An assembly selection bound to one loaded grid. Resolve it again before using its parts.</summary>
    public sealed class BlockAssemblyBinding
    {
        private readonly VoxelEntity grid;
        private readonly BrickCoord controller;
        private readonly BlockAssemblyPart[] selected;

        internal BlockAssemblyBinding(VoxelEntity grid, BrickCoord controller, BlockAssemblyPart[] selected)
        {
            this.grid = grid;
            this.controller = controller;
            this.selected = selected;
        }

        public bool IsComplete()
        {
            if (!TryReadController()) return false;
            foreach (var part in selected)
                if (!BlockAssemblyProvider.TryRead(grid, part.Placement, out _)) return false;
            return true;
        }

        public bool TryGetParts(out IReadOnlyList<BlockAssemblyPart> parts)
        {
            parts = Array.Empty<BlockAssemblyPart>();
            if (!TryReadController()) return false;
            var resolved = new BlockAssemblyPart[selected.Length];
            for (var i = 0; i < resolved.Length; i++)
            {
                var part = selected[i];
                if (!BlockAssemblyProvider.TryRead(grid, part.Placement, out var current)) return false;
                resolved[i] = new BlockAssemblyPart(part.Name, current);
            }
            parts = Array.AsReadOnly(resolved);
            return true;
        }

        internal bool MatchesController(BrickCoord candidate)
            => candidate.entityID == controller.entityID
                && ReferenceEquals(candidate.entity, grid)
                && BlockAssemblyProvider.SamePlacement(candidate.brickData, controller.brickData);

        internal bool TryGetController(out BrickCoord placement)
        {
            placement = default;
            return BlockAssemblyProvider.CanReadGrid(grid) && BlockAssemblyProvider.TryRead(grid, controller, out placement);
        }

        private bool TryReadController() => TryGetController(out _);
    }

    /// <summary>Resolves caller-selected parts on demand without scanning the world or retaining game IDs across loads.</summary>
    public static class BlockAssemblyProvider
    {
        private static int gameThread;

        public static bool TryBind(VoxelEntity grid, BrickCoord controller, IReadOnlyList<BlockAssemblyPart> parts,
            out BlockAssemblyBinding? assembly)
        {
            assembly = null;
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (parts.Count == 0 || !CanReadGrid(grid) || !TryRead(grid, controller, out var liveController))
                return false;
            var selected = new BlockAssemblyPart[parts.Count];
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<int> { liveController.brickData.instanceId };
            for (var i = 0; i < selected.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part.Name) || !names.Add(part.Name)
                    || !TryRead(grid, part.Placement, out var current) || !ids.Add(current.brickData.instanceId))
                    return false;
                selected[i] = new BlockAssemblyPart(part.Name, current);
            }
            assembly = new BlockAssemblyBinding(grid, liveController, selected);
            return true;
        }

        internal static void Install() => gameThread = Thread.CurrentThread.ManagedThreadId;

        internal static void Disable() => gameThread = 0;

        internal static bool CanRead => gameThread == Thread.CurrentThread.ManagedThreadId
            && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
            && !GameloopManager.IsNativeContainerAccessUnsafe;

        internal static bool CanReadGrid(VoxelEntity grid)
            => CanRead && grid != null && !ReferenceEquals(BlockEntityManager.instance, null) && ReferenceEquals(grid.ID.Entity, grid);

        internal static bool TryRead(VoxelEntity grid, BrickCoord recorded, out BrickCoord placement)
        {
            placement = default;
            if (recorded.entityID != grid.ID || recorded.brickData.instanceId <= 0) return false;
            var current = grid.Bricks.GetBrickData(recorded.brickData.instanceId);
            if (!current.IsValid || current.healthScore == 0 || !SamePlacement(current, recorded.brickData)) return false;
            placement = new BrickCoord(grid.ID, current);
            return true;
        }

        internal static bool SamePlacement(BrickInstanceData left, BrickInstanceData right)
            => left.instanceId == right.instanceId && left.brickId == right.brickId && left.gridLevel == right.gridLevel
                && left.gridPosition == right.gridPosition && left.rotation == right.rotation && left.scale == right.scale;
    }
}
