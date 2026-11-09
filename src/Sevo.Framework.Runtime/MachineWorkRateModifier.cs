using System;
using System.Collections.Generic;
using VoxelEngine;
using VoxelEngine.Crafting;

namespace Sevo.Framework.Runtime
{
    /// <summary>Modifies powered work rate for Keel custom recipes in one live Arc Furnace.</summary>
    public static class MachineWorkRateModifier
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<(VoxelEntityID Entity, int Instance), Modifier> Attached =
            new Dictionary<(VoxelEntityID, int), Modifier>();
        private static bool enabled;

        /// <summary>
        /// Attaches one finite multiplier between 0.25 and 4 to a live furnace and module assembly.
        /// The stock rate resumes when the selected assembly is incomplete or the handle is disposed.
        /// </summary>
        public static bool TryAttach(BlockAssemblyBinding assembly, float multiplier, out IDisposable? handle)
        {
            handle = null;
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (!MachineWorkProgress.IsValidRate(multiplier)) throw new ArgumentOutOfRangeException(nameof(multiplier));
            if (!enabled || !assembly.TryGetController(out var furnace) || !assembly.IsComplete()
                || furnace.Command is not FurnaceCommandData data || data.isCrucible)
                return false;

            var key = (furnace.entityID, furnace.brickData.instanceId);
            lock (Gate)
            {
                if (Attached.ContainsKey(key)) return false;
                var modifier = new Modifier(key, assembly, multiplier);
                Attached.Add(key, modifier);
                handle = modifier;
                return true;
            }
        }

        internal static void Install() => enabled = true;

        internal static void Disable()
        {
            enabled = false;
            ResetWorld();
        }

        internal static void ResetWorld()
        {
            lock (Gate) Attached.Clear();
        }

        internal static float GetRate(MachineNativeState machine)
        {
            if (!enabled || machine.Kind != MachineKind.Furnace) return 1f;
            var placement = machine.Placement;
            Modifier? modifier;
            lock (Gate) Attached.TryGetValue((placement.entityID, placement.brickData.instanceId), out modifier);
            return modifier != null && modifier.Assembly.IsComplete() && modifier.Assembly.MatchesController(placement)
                ? modifier.Multiplier : 1f;
        }

        private sealed class Modifier : IDisposable
        {
            private readonly (VoxelEntityID Entity, int Instance) key;

            internal Modifier((VoxelEntityID Entity, int Instance) key, BlockAssemblyBinding assembly, float multiplier)
            {
                this.key = key;
                Assembly = assembly;
                Multiplier = multiplier;
            }

            internal BlockAssemblyBinding Assembly { get; }
            internal float Multiplier { get; }

            public void Dispose()
            {
                lock (Gate)
                    if (Attached.TryGetValue(key, out var current) && ReferenceEquals(current, this)) Attached.Remove(key);
            }
        }
    }
}
