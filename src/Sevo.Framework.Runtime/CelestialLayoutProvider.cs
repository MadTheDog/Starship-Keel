using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Common.RNG;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Applies namespaced body layout declarations to the stock system catalog in managed solo play.</summary>
    public static class CelestialLayoutProvider
    {
        private const int BodyLimit = 8;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CelestialLayoutRule> Rules = new Dictionary<string, CelestialLayoutRule>(StringComparer.Ordinal);
        private static readonly HashSet<ulong> ReportedSystems = new HashSet<ulong>();
        private static CelestialLayoutPlan? plan;
        private static Action<string>? warning;
        private static int gameThreadId;

        public static bool IsAvailable { get; private set; }

        public static void Register(string ownerId, string localName, CelestialLayoutRule rule)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            lock (Gate)
            {
                if (plan != null) throw new InvalidOperationException("Celestial layout registration is closed.");
                if (Rules.ContainsKey(key)) throw new InvalidOperationException("Duplicate celestial layout key: " + key);
                Rules.Add(key, rule);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var source = typeof(SolarBodyGenerator).GetMethod(nameof(SolarBodyGenerator.GetStarSystemBodiesData),
                new[] { typeof(StarData) }) ?? throw new MissingMethodException(nameof(SolarBodyGenerator.GetStarSystemBodiesData));
            var postfix = typeof(CelestialLayoutProvider).GetMethod(nameof(AfterGenerate), BindingFlags.Static | BindingFlags.NonPublic)!;
            warning = reportWarning;
            gameThreadId = Environment.CurrentManagedThreadId;
            harmony.Patch(source, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static void AfterGenerate(StarData __0, ref List<AstronData> __result)
        {
            if (!IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            if (Environment.CurrentManagedThreadId != gameThreadId)
            {
                Disable();
                warning!("Celestial layout disabled because stock generation ran off the game thread.");
                return;
            }

            IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>> declarations;
            lock (Gate)
            {
                plan ??= CelestialLayoutPlan.Resolve(Rules);
                declarations = plan.GetRules(__0.seed);
            }
            if (declarations.Count == 0) return;
            try
            {
                if (GameIntroductionResources.IsStartingSystem(__0))
                    throw new InvalidOperationException("The stock introduction system is protected.");
                if (CelestialLayoutPlan.HasConflict(declarations))
                    throw new InvalidOperationException("Conflicting declarations target the same body identity or field.");
                __result = BuildLayout(__0.seed, __result, declarations);
            }
            catch (Exception exception)
            {
                if (ReportedSystems.Add(__0.seed))
                    warning!("Celestial layout retained stock system " + __0.seed + " for "
                        + string.Join(", ", declarations.Select(entry => entry.Key)) + ". " + exception.Message);
            }
        }

        private static List<AstronData> BuildLayout(ulong systemSeed, List<AstronData> stock,
            IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>> declarations)
        {
            if (stock.Count == 0 || stock.Count > BodyLimit || stock[0].seed != systemSeed
                || stock[0].orbitalBodyType != OrbitalBodyType.star)
                throw new InvalidOperationException("Unexpected stock body list.");
            var candidate = new List<AstronData>(stock);
            var ids = new Dictionary<ulong, int>();
            var changed = new bool[BodyLimit];
            var changedOrbits = new bool[BodyLimit];
            for (int index = 0; index < stock.Count; index++)
            {
                var body = stock[index];
                if (body.id != index || body.seed == 0 || (index != 0 && (uint)body.parentId >= (uint)index)
                    || ids.ContainsKey(body.seed))
                    throw new InvalidOperationException("Stock body identities or parent ordering changed.");
                ids.Add(body.seed, index);
            }
            foreach (var declaration in declarations.Where(entry => !entry.Value.IsAddition))
            {
                var rule = declaration.Value;
                if (!ids.TryGetValue(rule.BodySeed, out int index) || index == 0)
                    throw new InvalidOperationException("An override target is absent or is the primary star.");
                var body = candidate[index];
                if (rule.RadiusMeters is float radius)
                {
                    if (body.hasRing) throw new InvalidOperationException("Radius overrides require a body without a native ring.");
                    RequireRadius(body.orbitalBodyType, body.celestialBodyType, radius);
                    body.radius = radius;
                }
                if (rule.OrbitRadiusMeters is float orbit)
                {
                    changedOrbits[index] = true;
                    body.orbitSemiRadius = orbit;
                    UpdateOrbitPosition(ref body);
                }
                candidate[index] = body;
                changed[index] = true;
            }
            foreach (var declaration in declarations.Where(entry => entry.Value.IsAddition))
            {
                var rule = declaration.Value;
                if (candidate.Count == BodyLimit || ids.ContainsKey(rule.BodySeed)
                    || !ids.TryGetValue(rule.ParentSeed!.Value, out int parentIndex)
                    || parentIndex >= stock.Count || candidate[parentIndex].orbitalBodyType != OrbitalBodyType.planet)
                    throw new InvalidOperationException("A moon addition exceeds stock capacity, duplicates an identity, or has no stock planet parent.");
                var parent = candidate[parentIndex];
                int nextSatellite = candidate.Where(body => body.id != 0 && body.parentId == parentIndex)
                    .Select(body => body.satelliteId + 1).DefaultIfEmpty(0).Max();
                var moon = new AstronData
                {
                    id = candidate.Count, seed = rule.BodySeed, parentId = parentIndex, satelliteId = nextSatellite,
                    orbitalBodyType = OrbitalBodyType.moon, celestialBodyType = AstronType.telluric, radius = rule.RadiusMeters!.Value
                };
                RequireRadius(moon.orbitalBodyType, moon.celestialBodyType, moon.radius);
                var hash = new HashDrawSet(moon.seed, PlanetGenerationDrawIds.MAX_ID);
                SolarBody.GenerateOrbitalParameters(hash, parent, ref moon);
                moon.orbitSemiRadius = rule.OrbitRadiusMeters!.Value;
                changedOrbits[moon.id] = true;
                UpdateOrbitPosition(ref moon);
                moon.name = SolarBody.CreateName(ref moon, parent.name);
                moon.hasRing = SolarBody.SetHasPlanetaryRing(hash, moon.celestialBodyType, moon.orbitalBodyType, false);
                changed[moon.id] = true;
                ids.Add(moon.seed, moon.id);
                candidate.Add(moon);
                parent.satellitesCount = candidate.Count(body => body.id != 0 && body.parentId == parentIndex);
                candidate[parentIndex] = parent;
            }

            for (int index = 1; index < candidate.Count; index++)
            {
                var body = candidate[index];
                var parent = candidate[body.parentId];
                if (changedOrbits[index]) RequireOrbit(body, parent, body.orbitSemiRadius);
                body.systemPosition = parent.systemPosition + body.orbitPosition;
                candidate[index] = body;
                if (changed[index] || changed[body.parentId])
                {
                    changed[index] = true;
                    if (body.orbitPosition.Magnitude <= parent.radius + body.radius)
                        throw new InvalidOperationException("A changed body overlaps its parent.");
                }
                for (int otherIndex = 1; otherIndex < index; otherIndex++)
                {
                    var other = candidate[otherIndex];
                    if ((changed[index] || changed[otherIndex])
                        && (body.systemPosition - other.systemPosition).Magnitude <= body.radius + other.radius)
                        throw new InvalidOperationException("A changed body overlaps another body.");
                }
            }
            return candidate;
        }

        private static void RequireRadius(OrbitalBodyType orbit, AstronType type, float radius)
        {
            var limits = orbit == OrbitalBodyType.moon ? (14000f, 61000f)
                : type == AstronType.gas ? (63000f, 275000f) : (42000f, 184000f);
            if (radius < limits.Item1 || radius > limits.Item2)
                throw new InvalidOperationException("A declared body radius is outside the preview's stock-sized range.");
        }

        private static void RequireOrbit(AstronData body, AstronData parent, float orbit)
        {
            if (orbit > 200000000f || orbit < 2f * (parent.radius + body.radius)
                || (parent.hasRing && (!(parent.ringOuterRadius > 0) || orbit < 2f * parent.ringOuterRadius)))
                throw new InvalidOperationException("A declared orbit is outside supported clearance or its parent ring is unresolved.");
        }

        private static void UpdateOrbitPosition(ref AstronData body)
        {
            Vector3 relative = body.ellipse.Evaluate(2f * Mathf.PI * body.angularPosition);
            body.orbitPosition = new Vector3l(body.orbitSemiRadius * relative);
        }
    }
}
