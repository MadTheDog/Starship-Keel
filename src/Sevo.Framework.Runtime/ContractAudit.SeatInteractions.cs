using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string SeatType = "VoxelEngine.EnterCommandSeat";
        private const string SeatPlayerType = "Game.PlayerCommandManager";
        private const string CharacterAnimatorType = "Game.CharacterAnimator";

        public static CapabilityResult EvaluateSeatedPoseTargets(ModuleDefinition module)
        {
            const string capability = "seatedPoseTargets";
            try
            {
                var control = ContextMethod(module, SeatType, "OnControl", false, "System.Void", SeatPlayerType);
                var position = ContextMethod(module, SeatType, "PositionCharacterBody", false, "System.Void", SeatPlayerType);
                var exit = ContextMethod(module, SeatType, "OnExit", false, "System.Void", SeatPlayerType);
                var seated = ContextMethod(module, SeatPlayerType, "set_IsSeated", false, "System.Void", "System.Boolean");
                var ik = ContextMethod(module, CharacterAnimatorType, "OnAnimatorIK", false, "System.Void", "System.Int32");
                var getAnimator = ContextMethod(module, CharacterAnimatorType, "get_animator", false, "UnityEngine.Animator");
                var update = ContextMethod(module, "Game.CharacterIKTarget", "Update", false, "System.Void", "System.Int32");
                var computeDistance = ContextMethod(module, "Game.CharacterIKTarget", "ComputeDistanceToTarget", false, "System.Void");
                if (!ContextCalls(control, position) || !ContextCalls(exit, seated)
                    || !ContextCalls(ik, getAnimator) || !ContextCalls(ik, update)
                    || !CallsSeatEngine(update, "UnityEngine.Animator", "SetIKPosition", "System.Void",
                        "UnityEngine.AvatarIKGoal", "UnityEngine.Vector3")
                    || !CallsSeatEngine(update, "UnityEngine.Animator", "SetIKPositionWeight", "System.Void",
                        "UnityEngine.AvatarIKGoal", "System.Single")
                    || !CallsSeatEngine(computeDistance, "UnityEngine.Animator", "GetBoneTransform", "UnityEngine.Transform",
                        "UnityEngine.HumanBodyBones"))
                    throw new InvalidOperationException("Native seated pose or humanoid IK flow changed.");

                return new CapabilityResult(capability, true, "Native seated pose and humanoid IK flow validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateDismountPoses(ModuleDefinition module)
        {
            const string capability = "dismountPoses";
            try
            {
                var postExit = ContextMethod(module, SeatType, "OnPostExit", false, "System.Boolean", SeatPlayerType);
                var update = ContextMethod(module, "Network.NetworkedCommandActivator", "OnUpdate", false, "System.Void");
                var inheritedPostExit = ContextMethod(module, "VoxelEngine.EnterCommand", "OnPostExit", false, "System.Boolean", SeatPlayerType);
                const string physics = "VoxelEngine.VoxelPhysics";
                const string query = "Manager.SkyPhysics/QueryContext";
                const string vector = "UnityEngine.Vector3";
                const string scalar = "System.Single";
                var push = ContextMethod(module, physics, "PushCapsule", true, vector, vector, scalar,
                    vector, scalar, vector + "&", query);
                var move = ContextMethod(module, physics, "OffsetCapsuleMove", true, vector, vector,
                    vector, scalar, vector, vector, query);
                if (!move.IsPublic || !ContextCalls(move, push)
                    || !ContextCalls(update, inheritedPostExit)
                    || !postExit.Body.Instructions.Any(instruction =>
                        (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                        && instruction.Operand is MethodReference method && method.Name == "set_newSystemPosition"
                        && method.DeclaringType.FullName == SeatPlayerType))
                    throw new InvalidOperationException("Native seat exit or capsule collision flow changed.");

                return new CapabilityResult(capability, true, "Native seat exit and capsule collision flow validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool CallsSeatEngine(MethodDefinition method, string type, string name,
            string result, params string[] parameters)
            => method.HasBody && method.Body.Instructions.Any(instruction =>
                (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference called && called.DeclaringType.FullName == type
                && called.Name == name && called.ReturnType.FullName == result
                && called.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));
    }
}
