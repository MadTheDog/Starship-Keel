using System;
using System.Reflection;
using Common;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Changes submitted render records, never saved instances or physics definitions.</summary>
    internal static class ReceiverModularRendering
    {
        internal static void Install(Harmony harmony)
        {
            var register = AccessTools.Method(typeof(Brick), "RegisterRenderData", new[]
            {
                typeof(BrickInstanceData), typeof(EvoRenderer), typeof(int), typeof(BrickIds).MakeByRefType(), typeof(bool)
            }) ?? throw new InvalidOperationException("Native brick render registration is unavailable.");
            var update = AccessTools.Method(typeof(Brick), "UpdateRenderData", new[]
            {
                typeof(BrickInstanceData), typeof(EvoRenderer), typeof(int), typeof(BrickIds),
                typeof(VoxelEntity), typeof(bool), typeof(bool)
            }) ?? throw new InvalidOperationException("Native brick render updates are unavailable.");
            var set = AccessTools.Method(typeof(Brick), "SetBrickData", new[]
            {
                typeof(RenderData).MakeByRefType(), typeof(BrickInstanceData), typeof(int), typeof(int), typeof(bool)
            }) ?? throw new InvalidOperationException("Native render record binding is unavailable.");
            var registerPatch = Patch(nameof(RegisterLegacyRepeat));
            var updatePatch = Patch(nameof(UpdateLegacyRepeat));
            var setPatch = Patch(nameof(AdjustSubmittedGeometry));
            try
            {
                harmony.Patch(register, prefix: new HarmonyMethod(registerPatch));
                harmony.Patch(update, prefix: new HarmonyMethod(updatePatch));
                harmony.Patch(set, postfix: new HarmonyMethod(setPatch));
            }
            catch
            {
                // The caller may use this Harmony owner for other capabilities.
                harmony.Unpatch(register, registerPatch);
                harmony.Unpatch(update, updatePatch);
                harmony.Unpatch(set, setPatch);
                throw;
            }
        }

        private static MethodInfo Patch(string name) => typeof(ReceiverModularRendering)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing modular rendering hook: " + name);

        private static bool RegisterLegacyRepeat(Brick __instance, BrickInstanceData __0, EvoRenderer __1,
            int __2, ref BrickIds __3, bool __4)
        {
            var definition = FunctionalStockTemplate.GetModularLegacyRepeat(__0, __3);
            if (definition == null || ReferenceEquals(definition, __instance)) return true;
            // BrickInstanceData is a value: the persisted record remains untouched.
            var rendering = __0;
            rendering.brickId = checked((ushort)definition.brickID);
            definition.RegisterRenderData(rendering, __1, __2, ref __3, __4);
            return false;
        }

        private static bool UpdateLegacyRepeat(Brick __instance, BrickInstanceData __0, EvoRenderer __1,
            int __2, BrickIds __3, VoxelEntity __4, bool __5, bool __6)
        {
            var definition = FunctionalStockTemplate.GetModularLegacyRepeat(__0, __3);
            if (definition == null || ReferenceEquals(definition, __instance)) return true;
            var rendering = __0;
            rendering.brickId = checked((ushort)definition.brickID);
            definition.UpdateRenderData(rendering, __1, __2, __3, __4, __5, __6);
            return false;
        }

        private static void AdjustSubmittedGeometry(ref RenderData __0, BrickInstanceData __1)
        {
            var definition = __1.Brick;
            if (FunctionalStockTemplate.TryGetModularReceiver(definition, out var repeat, out var bay))
            {
                // Preview repeat submission also carries the HOST's instance data.
                // Identity alone is insufficient; only the host's own shape ranges disappear.
                if (Matches(__0, definition.VoxelMesh))
                {
                    // SetRenderData already passed its nonzero guard. Retain the resulting
                    // renderId1 so native updates and FreeIndex still own the data allocation.
                    __0.clusterCount = 0;
                    __0.clusterCountLOD = 0;
                    return;
                }
                if (Matches(__0, repeat.VoxelMesh))
                    __0.bounds = RenderBounds(bay, __1, definition);
            }
            else if (FunctionalStockTemplate.TryGetModularBay(definition, out bay)
                && Matches(__0, definition.VoxelMesh))
                __0.bounds = RenderBounds(bay, __1, definition);
        }

        private static bool Matches(RenderData submitted, VoxelMesh mesh) => mesh != null
            && (SameSpan(submitted, mesh.renderData1) || SameSpan(submitted, mesh.renderData2)
                || SameSpan(submitted, mesh.renderData3));

        private static bool SameSpan(RenderData submitted, RenderData source) => source.clusterCount > 0
            && submitted.clusterStart == source.clusterStart && submitted.clusterCount == source.clusterCount
            && submitted.clusterStartLOD == source.clusterStartLOD && submitted.clusterCountLOD == source.clusterCountLOD;

        private static Aabb RenderBounds(Bounds bay, BrickInstanceData data, Brick definition)
        {
            // Mirror GetAabb(true), using only the admitted render envelope. Native grid
            // rotations are signed axis permutations. Preview may retain the host scale.
            bay.max += definition.Unit * data.ScaleFloat;
            float grid = data.GridSize;
            var rotation = data.Quaternion;
            bay.center = data.Position + rotation * (grid * bay.center);
            var size = rotation * (grid * bay.size);
            bay.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            return new Aabb(bay);
        }
    }
}
