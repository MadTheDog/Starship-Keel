using System;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public readonly struct SeatedFootTargets
    {
        public Vector3? Left { get; }
        public Vector3? Right { get; }

        public SeatedFootTargets(Vector3? left, Vector3? right)
        {
            Left = left;
            Right = right;
        }
    }

    /// <summary>Supplies optional foot positions in the occupied seat's block-local coordinates.</summary>
    public static class SeatedPoseTargetProvider
    {
        private static readonly SeatProviderCatalog<SeatedFootTargets> Providers = new SeatProviderCatalog<SeatedFootTargets>();
        private static AnimatorContract? contract;
        private static PlayerCommandManager? player;
        private static Vector3l? leftSystem;
        private static Vector3l? rightSystem;
        private static int frame = -1;
        private static object? lastAnimator;
        private static bool hadLeft;
        private static bool hadRight;

        public static void Register(string ownerId, string localName, Func<SeatInteractionContext, SeatedFootTargets?> provider)
            => Providers.Register(ownerId, localName, provider);

        internal static void Install(Harmony harmony, Action<string> report)
        {
            var methods = new AnimatorContract();
            harmony.Patch(AccessTools.Method(typeof(EnterCommandSeat), "OnControl", new[] { typeof(PlayerCommandManager) }),
                postfix: new HarmonyMethod(typeof(SeatedPoseTargetProvider), nameof(AfterControl)));
            harmony.Patch(AccessTools.Method(typeof(EnterCommandSeat), "OnExit", new[] { typeof(PlayerCommandManager) }),
                postfix: new HarmonyMethod(typeof(SeatedPoseTargetProvider), nameof(AfterExit)));
            harmony.Patch(AccessTools.Method(typeof(CharacterAnimator), nameof(CharacterAnimator.OnAnimatorIK), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(SeatedPoseTargetProvider), nameof(AfterAnimator)));
            Providers.Report = report;
            contract = methods;
        }

        internal static void Disable()
        {
            contract = null;
            Providers.Disable();
            ClearPose();
            lastAnimator = null;
            hadLeft = hadRight = false;
        }

        private static void AfterControl(EnterCommandSeat __instance, PlayerCommandManager __0)
        {
            if (contract == null || !ReferenceEquals(Dependency.PlayerManager, __0)) return;
            ClearPose();
            if (!__0.IsSeated || !SeatInteractionContext.TryGet(__instance, __0, out var seat)
                || !Providers.TryResolve(seat, out var targets)
                || !SeatInteractionContext.TryGet(__instance, __0, out seat))
                return;

            leftSystem = ResolveSystem(seat.Seat, targets.Left);
            rightSystem = ResolveSystem(seat.Seat, targets.Right);
            player = __0;
            frame = Time.frameCount;
        }

        private static void AfterExit(PlayerCommandManager __0)
        {
            if (ReferenceEquals(player, __0)) ClearPose();
        }

        private static void ClearPose()
        {
            player = null;
            leftSystem = rightSystem = null;
            frame = -1;
        }

        private static Vector3l? ResolveSystem(BrickCoord seat, Vector3? offset)
        {
            if (offset is not Vector3 local || !SeatInteractionContext.IsFinite(local) || local.sqrMagnitude > 4f) return null;
            return seat.TransformData.LocalToSystem(seat.BlockToEntity(local));
        }

        private static void AfterAnimator(CharacterAnimator __instance)
        {
            var methods = contract;
            var local = Dependency.PlayerManager;
            if (methods == null || ReferenceEquals(local, null) || !ReferenceEquals(local.CharacterAnimator, __instance)
                || !__instance.enabled) return;
            var active = FunctionalCommandState.IsSolo && ReferenceEquals(local, player) && local.IsSeated
                && (frame == Time.frameCount || frame == Time.frameCount - 1) && !PlayerState.IsQuittingToDesktopOrTitle;
            if (!active && !hadLeft && !hadRight) return;

            try
            {
                var animator = methods.GetAnimator.Invoke(__instance, null) as UnityEngine.Object;
                if (animator == null || methods.IsHuman.Invoke(animator, null) is not true)
                {
                    lastAnimator = null;
                    hadLeft = hadRight = false;
                    return;
                }
                if (!ReferenceEquals(lastAnimator, animator))
                {
                    lastAnimator = animator;
                    hadLeft = hadRight = false;
                }
                Apply(methods, animator, methods.LeftGoal, methods.LeftBone, active ? leftSystem : null, ref hadLeft);
                Apply(methods, animator, methods.RightGoal, methods.RightBone, active ? rightSystem : null, ref hadRight);
            }
            catch (Exception exception)
            {
                ClearPose();
                Providers.Report?.Invoke("Seated foot targets failed: " + exception.GetBaseException().Message);
            }
        }

        private static void Apply(AnimatorContract methods, UnityEngine.Object animator, object goal, object bone,
            Vector3l? target, ref bool applied)
        {
            if (target is Vector3l systemPosition)
            {
                var position = PositionManager.ToWorldPosition(systemPosition);
                if (SeatInteractionContext.IsFinite(position)
                    && methods.GetBone.Invoke(animator, new[] { bone }) is Transform ankle
                    && ankle != null && (ankle.position - position).sqrMagnitude <= 0.65f * 0.65f)
                {
                    methods.SetPosition.Invoke(animator, new object[] { goal, position });
                    methods.SetWeight.Invoke(animator, new object[] { goal, 1f });
                    applied = true;
                    return;
                }
            }
            if (applied)
            {
                methods.SetWeight.Invoke(animator, new object[] { goal, 0f });
                applied = false;
            }
        }

        private sealed class AnimatorContract
        {
            internal MethodInfo GetAnimator { get; }
            internal MethodInfo IsHuman { get; }
            internal MethodInfo GetBone { get; }
            internal MethodInfo SetPosition { get; }
            internal MethodInfo SetWeight { get; }
            internal object LeftGoal { get; }
            internal object RightGoal { get; }
            internal object LeftBone { get; }
            internal object RightBone { get; }

            internal AnimatorContract()
            {
                GetAnimator = AccessTools.PropertyGetter(typeof(CharacterAnimator), "animator")
                    ?? throw new InvalidOperationException("The native character animator getter is unavailable.");
                var animator = GetAnimator.ReturnType;
                var goal = animator.Assembly.GetType("UnityEngine.AvatarIKGoal", throwOnError: true)!;
                var bone = animator.Assembly.GetType("UnityEngine.HumanBodyBones", throwOnError: true)!;
                LeftGoal = Enum.Parse(goal, "LeftFoot");
                RightGoal = Enum.Parse(goal, "RightFoot");
                LeftBone = Enum.Parse(bone, "LeftFoot");
                RightBone = Enum.Parse(bone, "RightFoot");
                IsHuman = RequireMethod(animator, "get_isHuman", typeof(bool));
                GetBone = RequireMethod(animator, "GetBoneTransform", typeof(Transform), bone);
                SetPosition = RequireMethod(animator, "SetIKPosition", typeof(void), goal, typeof(Vector3));
                SetWeight = RequireMethod(animator, "SetIKPositionWeight", typeof(void), goal, typeof(float));
            }

            private static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] parameters)
            {
                var method = type.GetMethod(name, parameters);
                return method != null && !method.IsStatic && method.ReturnType == result ? method
                    : throw new InvalidOperationException("Native humanoid IK method changed: " + name);
            }
        }
    }
}
