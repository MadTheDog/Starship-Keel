using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal static class ReceiverRepeatRendering
    {
        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(Brick), "UpdateRenderData", new[]
            {
                typeof(BrickInstanceData), typeof(EvoRenderer), typeof(int), typeof(BrickIds),
                typeof(VoxelEntity), typeof(bool), typeof(bool)
            }) ?? throw new InvalidOperationException("The native receiver render update is unavailable.");
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(ReceiverRepeatRendering), nameof(UseRepeatRenderDefinition)));
        }

        private static IEnumerable<CodeInstruction> UseRepeatRenderDefinition(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var body = instructions.ToList();
            var calls = body.Where(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, original)).ToArray();
            int call = calls.Length == 1 ? body.IndexOf(calls[0]) : -1;
            int receiver = call - 8;
            if (receiver < 0 || !body[receiver].IsLdarg(0) || !HasLocalType(body[receiver + 1], original, typeof(BrickInstanceData))
                || !body[receiver + 2].IsLdarg(2) || !body[receiver + 3].IsLdarg(3)
                || !HasLocalType(body[receiver + 4], original, typeof(BrickIds))
                || !body[receiver + 5].IsLdarg(5) || !body[receiver + 6].IsLdarg(6)
                || body[receiver + 7].opcode != OpCodes.Ldc_I4_1)
                throw new InvalidOperationException("The live native repeat render operands changed.");

            // Harmony exposes the label list through a Mono-specific type.
            var labels = typeof(CodeInstruction).GetField("labels");
            if (body.Skip(receiver + 1).Take(8).Any(instruction =>
                (labels?.GetValue(instruction) as ICollection)?.Count != 0 || instruction.blocks.Count != 0))
                throw new InvalidOperationException("Native control flow enters the repeat render operands directly.");

            var selector = typeof(ReceiverRepeatRendering).GetMethod(nameof(SelectRepeatDefinition), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The managed repeat render selector is unavailable.");
            body.Insert(receiver + 1, new CodeInstruction(body[receiver + 1].opcode, body[receiver + 1].operand));
            body.Insert(receiver + 2, new CodeInstruction(OpCodes.Call, selector));
            return body;
        }

        private static bool HasLocalType(CodeInstruction instruction, MethodBase original, Type expected)
        {
            int index = instruction.opcode == OpCodes.Ldloc_0 ? 0
                : instruction.opcode == OpCodes.Ldloc_1 ? 1
                : instruction.opcode == OpCodes.Ldloc_2 ? 2
                : instruction.opcode == OpCodes.Ldloc_3 ? 3 : -1;
            if (instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S)
            {
                if (instruction.operand is LocalBuilder local) return local.LocalType == expected;
                if (instruction.operand is IConvertible operand) index = operand.ToInt32(null);
            }
            var locals = original.GetMethodBody()?.LocalVariables;
            return locals != null && (uint)index < locals.Count && locals[index].LocalType == expected;
        }

        private static Brick SelectRepeatDefinition(Brick host, BrickInstanceData repeat) =>
            FunctionalStockTemplate.ShouldUseReceiverRepeatRenderDefinition(host, repeat.brickId)
            && repeat.Brick is { isRepeatBrick: true } definition ? definition : host;
    }
}
