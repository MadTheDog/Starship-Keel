using System;
using Game;
using Manager;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Supplies a nearby seat-local exit position after stock dismount completes.</summary>
    public static class DismountPoseProvider
    {
        private static readonly SeatProviderCatalog<Vector3> Providers = new SeatProviderCatalog<Vector3>();
        private static bool enabled;

        public static void Register(string ownerId, string localName, Func<SeatInteractionContext, Vector3?> provider)
            => Providers.Register(ownerId, localName, provider);

        internal static void Install(Harmony harmony, Action<string> report)
        {
            harmony.Patch(AccessTools.Method(typeof(EnterCommandSeat), nameof(EnterCommandSeat.OnPostExit),
                new[] { typeof(PlayerCommandManager) }), postfix: new HarmonyMethod(typeof(DismountPoseProvider), nameof(AfterDismount)));
            Providers.Report = report;
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            Providers.Disable();
        }

        private static void AfterDismount(EnterCommandSeat __instance, PlayerCommandManager __0, bool __result)
        {
            if (!enabled || !__result || !SeatInteractionContext.TryGet(__instance, __0, out var seat)
                || !Providers.TryResolve(seat, out var local) || !SeatInteractionContext.IsFinite(local)
                || local.sqrMagnitude > 16f || !SeatInteractionContext.TryGet(__instance, __0, out seat))
                return;
            try
            {
                var stock = __0.newSystemPosition;
                var desired = seat.Seat.TransformData.LocalToSystem(seat.Seat.BlockToEntity(local));
                var origin = PositionManager.ToWorldPosition(stock);
                var target = PositionManager.ToWorldPosition(desired);
                if (!SeatInteractionContext.IsFinite(origin) || !SeatInteractionContext.IsFinite(target)) return;
                var move = target - origin;
                if (move.sqrMagnitude <= 0.000001f || move.sqrMagnitude > 1.25f * 1.25f) return;

                var body = __0.PlayerRigidbody;
                if (body == null) return;
                var axis = 0.5f * (__0.newRotation * Vector3.up);
                if (!SeatInteractionContext.IsFinite(axis) || !SeatInteractionContext.IsFinite(body.GravityForce)) return;

                // Check the path in bounded increments smaller than the stock capsule radius.
                var samples = Mathf.CeilToInt(move.magnitude / 0.1f);
                for (var step = 0; step <= samples; step++)
                {
                    var point = origin + (float)step / samples * move;
                    var correction = VoxelPhysics.OffsetCapsuleMove(Vector3.zero, point, 0.35f, axis,
                        body.GravityForce, SkyPhysics.CONTEXT);
                    if (!SeatInteractionContext.IsFinite(correction) || correction.sqrMagnitude > 0.0015f * 0.0015f)
                        return;
                }
                __0.newSystemPosition = desired;
            }
            catch (Exception exception)
            {
                Providers.Report?.Invoke("Dismount target rejected: " + exception.GetBaseException().Message);
            }
        }
    }
}
