using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Declares selectable block geometry that stock physical contact ignores.</summary>
    public static class SelectablePassThrough
    {
        private static readonly Dictionary<int, Brick> Definitions = new Dictionary<int, Brick>();
        internal static bool Enabled { get; private set; }

        /// <summary>Declares a stock hull shape with physical contact disabled. Stock build overlap rules still apply.</summary>
        public static void RegisterHull(string ownerId, string localName, int revision, string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("A display name is required.", nameof(displayName));
            ContentRegistration.Catalog.Register(ownerId, localName, revision, id =>
            {
                var brick = (Brick)StockShape.CreateHull(id, displayName);
                Configure(brick);
                return brick;
            }, () =>
            {
                if (!Enabled) return "Selectable pass-through geometry is disabled or unavailable.";
                return GameAdapter.IsStockHullAvailable(out var reason) ? null : reason;
            }, ContentKind.Ordinary);
        }

        internal static void Configure(Brick brick)
        {
            if (!Enabled || !ReferenceEquals(Brick.BRICK_INDEX[brick.brickID], brick))
                throw new InvalidOperationException("Selectable pass-through requires an admitted brick definition.");
            Definitions.Add(brick.brickID, brick);
            brick.IsCollisionOff = true;
        }

        internal static bool IsOwned(Brick brick)
            => Enabled && Definitions.TryGetValue(brick.brickID, out var owned) && ReferenceEquals(owned, brick)
                && ReferenceEquals(Brick.BRICK_INDEX[brick.brickID], brick);

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(VoxelPhysics), nameof(VoxelPhysics.IsOverlapping),
                new[] { typeof(BrickCoord), typeof(bool).MakeByRefType(), typeof(bool) }),
                transpiler: new HarmonyMethod(typeof(SelectablePassThrough), nameof(RestoreProposedPlacement)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SkyPhysics.QueryOverlapAabb),
                nameof(SkyPhysics.QueryOverlapAabb.OnElementIntersect),
                new[] { typeof(BrickColliderBVH.Element), typeof(int), typeof(bool), typeof(int) }),
                transpiler: new HarmonyMethod(typeof(SelectablePassThrough), nameof(RestoreExistingPlacement)) { priority = Priority.First });
            Enabled = true;
        }

        internal static void Disable()
        {
            Enabled = false;
            Definitions.Clear();
        }

        private static IEnumerable<CodeInstruction> RestoreProposedPlacement(IEnumerable<CodeInstruction> code)
        {
            var instructions = code.ToList();
            var flag = AccessTools.Field(typeof(Brick), nameof(Brick.IsCollisionOff));
            int index = instructions.FindIndex(instruction => instruction.LoadsField(flag));
            if (index < 0 || instructions.Count(instruction => instruction.LoadsField(flag)) != 1)
                throw new InvalidOperationException("The stock proposed-placement collision flag load changed.");
            instructions[index].opcode = OpCodes.Call;
            instructions[index].operand = AccessTools.DeclaredMethod(typeof(SelectablePassThrough), nameof(SkipPlacementOverlap));
            return instructions;
        }

        private static IEnumerable<CodeInstruction> RestoreExistingPlacement(IEnumerable<CodeInstruction> code)
        {
            var instructions = code.ToList();
            var lookup = AccessTools.PropertyGetter(typeof(Unity.Collections.NativeArray<BrickProperties>), "Item");
            var brickId = AccessTools.Field(typeof(BrickInstanceData), nameof(BrickInstanceData.brickId));
            var index = instructions.FindIndex(instruction => instruction.LoadsField(brickId));
            if (index < 0 || index + 1 >= instructions.Count || !instructions[index + 1].Calls(lookup))
                throw new InvalidOperationException("The stock existing-placement properties load changed.");
            // Change the copy used by placement. Native arrays and all physical queries retain their stock values.
            instructions.InsertRange(index + 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call,
                    AccessTools.DeclaredMethod(typeof(SelectablePassThrough), nameof(PlacementProperties)))
            });
            return instructions;
        }

        private static bool SkipPlacementOverlap(Brick brick) => brick.IsCollisionOff && !IsOwned(brick);

        private static BrickProperties PlacementProperties(BrickProperties properties, ref SkyPhysics.QueryOverlapAabb query,
            BrickColliderBVH.Element element)
        {
            int id = element.brickData.brickId;
            if (properties.isCollisionOff && query.queryType == SkyPhysics.QueryOverlapType.OverlapBrick
                && Definitions.TryGetValue(id, out var brick) && ReferenceEquals(Brick.BRICK_INDEX[id], brick))
                properties.isCollisionOff = false;
            return properties;
        }
    }
}
