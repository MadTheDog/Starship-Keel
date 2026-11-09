using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;
using World.Terrain;
using World.Terrain.Planet;

namespace Sevo.Framework.Runtime
{
    /// <summary>Declares environment settings for a seeded rocky planet or moon.</summary>
    public static class BodyEnvironmentProvider
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BodyEnvironmentRule> Rules = new Dictionary<string, BodyEnvironmentRule>(StringComparer.Ordinal);
        private static Lazy<IReadOnlyDictionary<BodyEnvironmentTarget, BodyEnvironmentOverride>>? plan;
        private static bool sealedRules;

        public static bool IsAvailable => plan != null;
        internal static void Disable() => plan = null;

        /// <summary>Register during the dependent plugin's Awake. Each declaration is scoped to one stable pair of seeds.</summary>
        public static void Register(string ownerId, string localName, BodyEnvironmentTarget target, BodyEnvironmentOverride settings)
        {
            var rule = new BodyEnvironmentRule(ownerId + ":" + localName, target, settings);
            lock (Gate)
            {
                if (sealedRules) throw new InvalidOperationException("Body environment declarations are closed for this process.");
                if (Rules.ContainsKey(rule.Key)) throw new InvalidOperationException("Duplicate body environment key: " + rule.Key);
                Rules.Add(rule.Key, rule);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var generation = AccessTools.DeclaredMethod(typeof(PlanetGenerationLogic), nameof(PlanetGenerationLogic.GetPlanetData),
                new[] { typeof(SolarBody), typeof(int), typeof(bool) })
                ?? throw new MissingMethodException("PlanetGenerationLogic.GetPlanetData");
            var physics = AccessTools.DeclaredMethod(typeof(SolarBody), "SetPhysicsData", Type.EmptyTypes)
                ?? throw new MissingMethodException("SolarBody.SetPhysicsData");
            harmony.Patch(generation, transpiler: new HarmonyMethod(BridgeMethod(nameof(BridgeEnvironmentStores))) { priority = Priority.Last });
            harmony.Patch(physics, transpiler: new HarmonyMethod(BridgeMethod(nameof(BridgeGravityStore))) { priority = Priority.Last });
            plan = new Lazy<IReadOnlyDictionary<BodyEnvironmentTarget, BodyEnvironmentOverride>>(() =>
            {
                lock (Gate)
                {
                    sealedRules = true;
                    return BodyEnvironmentPlan.Resolve(Rules.Values, reportWarning);
                }
            });
        }

        private static BodyEnvironmentOverride? Resolve(SolarBody body)
        {
            var current = plan;
            if (current == null || PlayerState.sessionMode != PlayerState.SessionMode.solo || body.AstronType != AstronType.telluric
                || body.planetGenerationId == 0 || body.parentStar.seed == 0) return null;

            return current.Value.TryGetValue(new BodyEnvironmentTarget(body.parentStar.seed, body.Seed), out var match) ? match : null;
        }

        private static IEnumerable<CodeInstruction> BridgeEnvironmentStores(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            var data = typeof(PlanetDensityGenerationData);
            var bridges = new[]
            {
                (Field: nameof(PlanetDensityGenerationData.waterAmount), Writer: nameof(StoreWater), Count: 1),
                (Field: nameof(PlanetDensityGenerationData.atmosphereDensity), Writer: nameof(StoreAtmosphere), Count: 1),
                (Field: nameof(PlanetDensityGenerationData.oxygenDensity), Writer: nameof(StoreOxygen), Count: 2),
                (Field: nameof(PlanetDensityGenerationData.planetColorPalette), Writer: nameof(StorePalette), Count: 1),
            };
            var resolved = generator.DeclareLocal(typeof(BodyEnvironmentOverride));
            foreach (var bridge in bridges)
            {
                var stores = FindStores(code, data, bridge.Field, bridge.Count);
                foreach (var store in stores)
                {
                    store.opcode = OpCodes.Ldloc;
                    store.operand = resolved;
                    code.Insert(code.IndexOf(store) + 1, new CodeInstruction(OpCodes.Call, BridgeMethod(bridge.Writer)));
                }
            }

            code.InsertRange(0, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, BridgeMethod(nameof(Resolve))),
                new CodeInstruction(OpCodes.Stloc, resolved),
            });
            return code;
        }

        private static IEnumerable<CodeInstruction> BridgeGravityStore(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var store = FindStores(code, typeof(PlanetGravityData), nameof(PlanetGravityData.g), 1).Single();
            var field = store.operand;
            var index = code.IndexOf(store);
            store.opcode = OpCodes.Ldarg_0;
            store.operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, BridgeMethod(nameof(ResolveGravity))));
            code.Insert(index + 2, new CodeInstruction(OpCodes.Stfld, field));
            return code;
        }

        private static CodeInstruction[] FindStores(List<CodeInstruction> code, Type type, string name, int count)
        {
            var field = AccessTools.DeclaredField(type, name);
            var stores = code.Where(instruction => instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field)).ToArray();
            if (stores.Length != count || stores.Any(store => store.blocks.Count != 0))
                throw new InvalidOperationException("The native " + type.Name + "." + name + " store changed.");
            return stores;
        }

        private static MethodInfo BridgeMethod(string name) => AccessTools.DeclaredMethod(typeof(BodyEnvironmentProvider), name);

        private static void StoreWater(ref PlanetDensityGenerationData data, float stock, BodyEnvironmentOverride? value)
            => data.waterAmount = value?.WaterAmount ?? stock;

        private static void StoreAtmosphere(ref PlanetDensityGenerationData data, float stock, BodyEnvironmentOverride? value)
            => data.atmosphereDensity = value?.AtmosphereDensity ?? stock;

        private static void StoreOxygen(ref PlanetDensityGenerationData data, float stock, BodyEnvironmentOverride? value)
            => data.oxygenDensity = value?.ResolveOxygen(stock, data.atmosphereDensity) ?? stock;

        private static float ResolveGravity(float stock, SolarBody body) => Resolve(body)?.GravityScale ?? stock;

        private static void StorePalette(ref PlanetDensityGenerationData data, PlanetColorLogic.PlanetColorPalette stock,
            BodyEnvironmentOverride? value)
        {
            stock.atmosphereColor = RgbOr(value?.AtmosphereRgb, stock.atmosphereColor);
            stock.oceanColorMain = RgbOr(value?.OceanMainRgb, stock.oceanColorMain);
            stock.oceanColorSec = RgbOr(value?.OceanSecondaryRgb, stock.oceanColorSec);
            stock.oceanColorDepth = RgbOr(value?.OceanDepthRgb, stock.oceanColorDepth);
            data.planetColorPalette = stock;
        }

        private static Color32 RgbOr(uint? rgb, Color32 stock) => rgb is uint color
            ? new Color32((byte)(color >> 16), (byte)(color >> 8), (byte)color, byte.MaxValue)
            : stock;
    }
}
