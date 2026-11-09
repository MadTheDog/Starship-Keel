using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mirror;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Observes confirmed stock grid destruction in an active solo session.</summary>
    public static class GridDestruction
    {
        private static readonly GridDestructionObservers Observers = new GridDestructionObservers();
        private static volatile Action<string>? report;
        private static int gameThreadId;

        /// <summary>Whether the audited observer hook is installed.</summary>
        public static bool IsAvailable => report != null;

        /// <summary>Registers a process-lifetime observer under an owner and local name.</summary>
        public static void RegisterObserver(string ownerId, string localName, Action<GridDestructionNotice> observer)
            => Observers.Add(ownerId, localName, observer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> warning)
        {
            var target = AccessTools.DeclaredMethod(typeof(ParentEntity), nameof(ParentEntity.OnGameplayDestructionServer), Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(ParentEntity.OnGameplayDestructionServer));
            var patch = typeof(GridDestruction);
            harmony.Patch(target, prefix: new HarmonyMethod(patch, nameof(BeforeDestruction)) { priority = Priority.Last },
                postfix: new HarmonyMethod(patch, nameof(AfterDestruction)) { priority = Priority.First });
            gameThreadId = Environment.CurrentManagedThreadId;
            report = warning;
        }

        internal static void Disable()
        {
            report = null;
            gameThreadId = 0;
        }

        private static bool IsActiveGrid(ParentEntity grid)
            => Environment.CurrentManagedThreadId == gameThreadId && NetworkServer.active
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && !PlayerState.InLoadingScreen
                && !PlayerState.IsQuittingToDesktopOrTitle && grid != null && grid.IsInitialized && !grid.WillBeUnspawned;

        private static void BeforeDestruction(ParentEntity __instance, out GridDestructionNotice? __state)
        {
            __state = null;
            var warning = report;
            if (warning == null || !Observers.HasObservers) return;
            try
            {
                if (!IsActiveGrid(__instance) || __instance.isDestroyed) return;
                var transform = __instance.SkyTransform;
                var identity = __instance.Identity;
                if (transform == null || identity == null) return;
                __state = new GridDestructionNotice(__instance.SGUID, __instance.EntityName, transform.CurrentSystem.seed,
                    identity.IsNPC, __instance.EntityMetaData.isWorldWreck);
            }
            catch (Exception exception)
            {
                GridDestructionObservers.ReportSafely(warning, "Grid destruction snapshot skipped: " + exception.Message);
            }
        }

        private static void AfterDestruction(ParentEntity __instance, GridDestructionNotice? __state, bool __runOriginal)
        {
            var warning = report;
            if (warning == null || !__runOriginal || !__state.HasValue) return;
            try
            {
                if (IsActiveGrid(__instance))
                    Observers.PublishTransition(__state, __instance.isDestroyed, warning);
            }
            catch (Exception exception)
            {
                GridDestructionObservers.ReportSafely(warning, "Grid destruction notification skipped: " + exception.Message);
            }
        }
    }
}
