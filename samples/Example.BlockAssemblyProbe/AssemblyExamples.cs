using System;
using System.Collections.Generic;
using Sevo.Framework.Runtime;
using VoxelEngine;

namespace Sevo.Example.BlockAssemblyProbe
{
    public static class AssemblyExamples
    {
        // Call after the mod has obtained a live yard. The resolver supplies the mod's current part selection.
        public static bool RequireShipyardFrameAndWelder(CommandShipyard yard, VoxelEntity grid,
            Func<(BrickCoord Frame, BrickCoord Welder)> readSelection, out IDisposable? handle)
        {
            return ShipyardAssemblyEligibility.TryRequireAssembly(yard, () =>
            {
                var (frame, welder) = readSelection();
                var parts = new[] { new BlockAssemblyPart("frame", frame), new BlockAssemblyPart("welder", welder) };
                return BlockAssemblyProvider.TryBind(grid, yard.brickCoord, parts, out var binding) ? binding : null;
            }, out handle);
        }

        // A separate consumer can discover and inspect linked furnace modules without a shipyard hook.
        public static bool TryReadFurnaceModule(VoxelEntity grid, BrickCoord furnace, BrickCoord module,
            out IReadOnlyList<BlockAssemblyPart> parts)
        {
            var selected = new[] { new BlockAssemblyPart("module", module) };
            if (BlockAssemblyProvider.TryBind(grid, furnace, selected, out var binding) && binding != null)
                return binding.TryGetParts(out parts);
            parts = Array.Empty<BlockAssemblyPart>();
            return false;
        }

        // The mod can associate its own selected module with a live Arc Furnace and keep the returned handle.
        public static bool AttachFurnaceWorkRateModule(VoxelEntity grid, BrickCoord furnace, BrickCoord module,
            out IDisposable? handle)
        {
            handle = null;
            var selected = new[] { new BlockAssemblyPart("module", module) };
            return BlockAssemblyProvider.TryBind(grid, furnace, selected, out var assembly) && assembly != null
                && MachineWorkRateModifier.TryAttach(assembly, 2f, out handle);
        }
    }
}
