using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sevo.Framework.Sdk;
using Unity.Collections;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Contributes visual overrides for existing named native terrain materials.</summary>
    public static class TerrainMaterialVisuals
    {
        private sealed class Rule
        {
            internal string MaterialName = "";
            internal float? TextureScale;
            internal float? BrightnessFactor;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Rule> Rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedAmbiguities = new HashSet<string>(StringComparer.Ordinal);
        private static Action<string>? warning;
        private static Lazy<IReadOnlyDictionary<string, Rule>>? resolved;
        private static bool sealedRules;

        public static bool IsAvailable { get; private set; }
        internal static void Disable() => IsAvailable = false;

        /// <summary>Declares a rule during dependent plugin Awake, before native material publication.</summary>
        public static void Register(string ownerId, string localName, string nativeMaterialName,
            float? textureScale = null, float? brightnessFactor = null)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (string.IsNullOrWhiteSpace(nativeMaterialName)) throw new ArgumentException("A native shader name is required.", nameof(nativeMaterialName));
            if (textureScale is float scale && !(scale > 0f && scale < float.PositiveInfinity))
                throw new ArgumentOutOfRangeException(nameof(textureScale));
            if (brightnessFactor is float brightness && !(brightness > 0f && brightness < float.PositiveInfinity))
                throw new ArgumentOutOfRangeException(nameof(brightnessFactor));
            if (textureScale == null && brightnessFactor == null) throw new ArgumentException("A visual override is required.");
            lock (Gate)
            {
                if (sealedRules) throw new InvalidOperationException("Terrain material declarations are closed for this process.");
                if (Rules.ContainsKey(key)) throw new InvalidOperationException("Duplicate terrain material override: " + key);
                Rules.Add(key, new Rule { MaterialName = nativeMaterialName, TextureScale = textureScale, BrightnessFactor = brightnessFactor });
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var target = typeof(WorldTerrainResources).GetMethod("PushMaterialDatas", BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException("WorldTerrainResources.PushMaterialDatas");
            var transpiler = typeof(TerrainMaterialVisuals).GetMethod(nameof(BridgeStockSetter), BindingFlags.Static | BindingFlags.NonPublic)!;
            warning = reportWarning;
            harmony.Patch(target, transpiler: new HarmonyMethod(transpiler) { priority = Priority.Last });
            resolved = new Lazy<IReadOnlyDictionary<string, Rule>>(ResolveRules);
            IsAvailable = true;
        }

        private static IReadOnlyDictionary<string, Rule> ResolveRules()
        {
            lock (Gate)
            {
                sealedRules = true;
                var plan = new Dictionary<string, Rule>(StringComparer.Ordinal);
                foreach (var group in Rules.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .GroupBy(entry => entry.Value.MaterialName, StringComparer.Ordinal))
                {
                    var entries = group.ToArray();
                    if (entries.Length == 1)
                        plan.Add(group.Key, entries[0].Value);
                    else
                        warning!("Terrain material retains stock values due to conflicting declarations for " + group.Key
                            + ": " + string.Join(", ", entries.Select(entry => entry.Key)));
                }
                return plan;
            }
        }

        private static IEnumerable<CodeInstruction> BridgeStockSetter(IEnumerable<CodeInstruction> instructions)
        {
            var setter = typeof(NativeArray<TerrainMaterialData>).GetProperty("Item")?.SetMethod
                ?? throw new MissingMethodException("Native terrain material indexer setter");
            var replacement = typeof(TerrainMaterialVisuals).GetMethod(nameof(SetVisualRow), BindingFlags.Static | BindingFlags.NonPublic)!;
            var code = instructions.ToList();
            var stock = code.SingleOrDefault(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, setter));
            if (stock == null || stock.blocks.Count != 0)
                throw new InvalidOperationException("Live terrain material setter is missing, ambiguous, or protected by a new exception boundary.");
            int index = code.IndexOf(stock);
            stock.opcode = OpCodes.Ldarg_0;
            stock.operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, replacement));
            return code;
        }

        private static void SetVisualRow(ref NativeArray<TerrainMaterialData> destination, int index, TerrainMaterialData row,
            WorldTerrainResources resources)
        {
            if (IsAvailable)
            {
                try
                {
                    var plan = resolved!.Value;
                    var materials = resources.terrainTextures;
                    if (plan.Count > 0 && (uint)index < (uint)materials.Count)
                    {
                        string name = materials[index].ShaderName;
                        if (name != null && plan.TryGetValue(name, out var rule))
                        {
                            if (materials.Count(material => string.Equals(material.ShaderName, name, StringComparison.Ordinal)) == 1)
                            {
                                row.textureScale = rule.TextureScale ?? row.textureScale;
                                row.brightnessFactor = rule.BrightnessFactor ?? row.brightnessFactor;
                            }
                            else if (ReportedAmbiguities.Add(name))
                                warning!("Terrain material retains stock values because its native name is ambiguous: " + name);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Disable();
                    warning!("Terrain material visual overrides disabled: " + exception.Message);
                }
            }
            destination[index] = row;
        }
    }
}
