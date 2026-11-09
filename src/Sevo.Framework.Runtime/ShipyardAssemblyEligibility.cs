using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Requires a complete assembly at a selected shipyard's native blueprint action.</summary>
    public static class ShipyardAssemblyEligibility
    {
        private static ConditionalWeakTable<CommandShipyard, Requirement> requirements =
            new ConditionalWeakTable<CommandShipyard, Requirement>();
        private static Action<string>? warn;

        /// <summary>
        /// Registers a live requirement for this yard. Return null from resolve when required parts are missing.
        /// Dispose when the requirement should be removed and re-register for the new command on world reload.
        /// </summary>
        public static bool TryRequireAssembly(CommandShipyard yard, Func<BlockAssemblyBinding?> resolve,
            out IDisposable? handle)
        {
            handle = null;
            if (yard == null) throw new ArgumentNullException(nameof(yard));
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            if (warn == null || !BlockAssemblyProvider.CanRead || yard.IsRemoved || ReferenceEquals(BlockEntityManager.instance, null)
                || yard.brickCoord.entity is not { } grid || !BlockAssemblyProvider.CanReadGrid(grid)
                || !BlockAssemblyProvider.TryRead(grid, yard.brickCoord, out var controller)
                || !ReferenceEquals(controller.Command, yard)
                || requirements.TryGetValue(yard, out _)) return false;
            var requirement = new Requirement(yard, resolve);
            requirements.Add(yard, requirement);
            handle = requirement;
            return true;
        }

        internal static void Install(Harmony harmony, Action<string> warning)
        {
            harmony.Patch(AccessTools.Method(typeof(CommandShipyard), nameof(CommandShipyard.CanSpawnBlueprint),
                new[] { typeof(EntityMetaData), typeof(Dictionary<int, int>), typeof(StringBuilder) }),
                prefix: new HarmonyMethod(typeof(ShipyardAssemblyEligibility), nameof(CheckPreview)));
            harmony.Patch(AccessTools.Method(typeof(CommandShipyard), nameof(CommandShipyard.SpawnBlueprintFromBlueprintSelection),
                new[] { typeof(EntityMetaData), typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(typeof(ShipyardAssemblyEligibility), nameof(CheckCommit)));
            warn = warning;
        }

        internal static void Disable()
        {
            warn = null;
            requirements = new ConditionalWeakTable<CommandShipyard, Requirement>();
        }

        private static bool CheckPreview(CommandShipyard __instance, ref bool __result)
        {
            if (Allows(__instance)) return true;
            __result = false;
            return false;
        }

        private static bool CheckCommit(CommandShipyard __instance) => Allows(__instance);

        private static bool Allows(CommandShipyard yard)
        {
            if (warn == null || !requirements.TryGetValue(yard, out var requirement) || !FunctionalCommandState.IsSolo)
                return true;
            if (!BlockAssemblyProvider.CanRead || yard.IsRemoved) return false;
            try
            {
                var assembly = requirement.Resolve();
                return assembly != null && assembly.IsComplete() && assembly.MatchesController(yard.brickCoord)
                    && ReferenceEquals(yard.brickCoord.Command, yard);
            }
            catch (Exception exception)
            {
                if (!requirement.Reported)
                {
                    requirement.Reported = true;
                    warn("Shipyard assembly resolution failed: " + exception.Message);
                }
                return false;
            }
        }

        private sealed class Requirement : IDisposable
        {
            private readonly CommandShipyard yard;
            internal Func<BlockAssemblyBinding?> Resolve { get; }
            internal bool Reported { get; set; }

            internal Requirement(CommandShipyard yard, Func<BlockAssemblyBinding?> resolve)
            {
                this.yard = yard;
                Resolve = resolve;
            }

            public void Dispose()
            {
                if (requirements.TryGetValue(yard, out var current) && ReferenceEquals(current, this))
                    requirements.Remove(yard);
            }
        }
    }
}
