using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Publishes bounded mod-owned survey results for an accepted stock scanner pulse.</summary>
    public static class OreDiscoveryProvider
    {
        private static readonly object Gate = new object();
        private static readonly SortedDictionary<string, Func<OreDiscoveryContext, IReadOnlyList<OreDiscoveryResult>>> Providers =
            new SortedDictionary<string, Func<OreDiscoveryContext, IReadOnlyList<OreDiscoveryResult>>>(StringComparer.Ordinal);
        private static OreDiscoverySnapshot? snapshot;
        private static Action<string>? warning;
        private static int gameThreadId;
        private static float snapshotPulse;
        private static bool evaluating;

        public static bool IsAvailable { get; private set; }

        /// <summary>Returns the active pulse's custom results on the game thread. Stock scanner groups are separate.</summary>
        public static OreDiscoverySnapshot? Latest
            => IsAvailable && Environment.CurrentManagedThreadId == gameThreadId ? ReadLatest() : null;

        public static void Register(string ownerId, string localName,
            Func<OreDiscoveryContext, IReadOnlyList<OreDiscoveryResult>> provider)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            lock (Gate)
            {
                if (Providers.ContainsKey(key)) throw new InvalidOperationException("Duplicate ore discovery provider: " + key);
                Providers.Add(key, provider);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var target = AccessTools.DeclaredMethod(typeof(OreScannerResources), nameof(OreScannerResources.OnScannerKeyPressed), Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(OreScannerResources.OnScannerKeyPressed));
            var reload = AccessTools.DeclaredMethod(typeof(OreScannerResources), nameof(OreScannerResources.OnGameReload), Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(OreScannerResources.OnGameReload));
            warning = reportWarning;
            gameThreadId = Environment.CurrentManagedThreadId;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(OreDiscoveryProvider), nameof(BeforePulse)),
                postfix: new HarmonyMethod(typeof(OreDiscoveryProvider), nameof(AfterPulse)) { priority = Priority.Last });
            harmony.Patch(reload, postfix: new HarmonyMethod(typeof(OreDiscoveryProvider), nameof(ClearSnapshot)));
            PlanetarySystem.OnNewSystem += ClearSnapshot;
            IsAvailable = true;
        }

        internal static void Disable()
        {
            IsAvailable = false;
            PlanetarySystem.OnNewSystem -= ClearSnapshot;
            ClearSnapshot();
        }

        private static void BeforePulse(out float __state) => __state = OreScannerResources.lastActivateTime;

        private static void AfterPulse(float __state)
        {
            if (!IsAvailable || evaluating || Environment.CurrentManagedThreadId != gameThreadId
                || OreScannerResources.lastActivateTime == __state || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            ClearSnapshot();
            var camera = Dependency.PlayerCameraComponent;
            var star = PlanetarySystem.CurrentStarData;
            if (camera == null || !star.IsValid || PlanetarySystem.InSystemTransition) return;

            KeyValuePair<string, Func<OreDiscoveryContext, IReadOnlyList<OreDiscoveryResult>>>[] providers;
            lock (Gate)
            {
                if (Providers.Count == 0) return;
                providers = Providers.ToArray();
            }
            evaluating = true;
            try
            {
                var origin = PositionManager.ToSystemPosition(camera.transform.position).ToDouble3();
                var context = new OreDiscoveryContext(star.seed, origin.x, origin.y, origin.z, OreScannerResources.PLAYER_SCANNER_RANGE);
                var results = new List<OreDiscoveryResult>();
                foreach (var provider in providers)
                {
                    if (results.Count == OreDiscoveryPlan.TotalLimit) break;
                    try
                    {
                        var offered = provider.Value(context);
                        if (offered == null) continue;
                        var contribution = new List<OreDiscoveryResult>();
                        OreDiscoveryPlan.Append(contribution, offered, context.RangeMeters);
                        results.AddRange(contribution.Take(OreDiscoveryPlan.TotalLimit - results.Count));
                    }
                    catch (Exception exception)
                    {
                        lock (Gate) Providers.Remove(provider.Key);
                        warning!("Ore discovery provider disabled: " + provider.Key + ". " + exception.Message);
                    }
                }
                snapshot = new OreDiscoverySnapshot(context, results);
                snapshotPulse = OreScannerResources.lastActivateTime;
            }
            catch (Exception exception)
            {
                Disable();
                warning!("Ore discovery disabled: " + exception.Message);
            }
            finally { evaluating = false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static OreDiscoverySnapshot? ReadLatest()
            => snapshot != null && PlayerState.sessionMode == PlayerState.SessionMode.solo && !PlanetarySystem.InSystemTransition
                && OreScannerResources.IsScannerRunning && OreScannerResources.lastActivateTime == snapshotPulse
                && PlanetarySystem.CurrentStarData.seed == snapshot.Context.SystemSeed ? snapshot : null;

        private static void ClearSnapshot() => snapshot = null;
    }
}
