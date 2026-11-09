using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateSublightControl(ModuleDefinition module)
        {
            const string id = "sublightControl";
            const string ship = "Spaceship";
            const string drive = "SpaceshipDrive";
            const string vehicle = "BaseVehicle";
            const string player = "Game.PlayerCommandManager";
            const string command = "VoxelEngine.EnterCommand";
            const string activator = "Network.NetworkedCommandActivator";
            const string parent = "VoxelEngine.ParentEntity";
            const string core = "VoxelEngine.ComputerBaseVehicle";
            const string input = "Game.VehicleControlInput";
            const string handle = "Manager.SkyRigidbody/Handle";
            const string @void = "System.Void";
            const string boolean = "System.Boolean";
            const string vec = "UnityEngine.Vector3";
            const string quat = "UnityEngine.Quaternion";
            try
            {
                if (!EvaluateGridPhysics(module).Available)
                    throw new InvalidOperationException("Grid physics contract is unavailable.");
                MethodDefinition Require(string type, string name, bool isPublic, string result, params string[] parameters)
                {
                    var method = ContextMethod(module, type, name, false, result, parameters);
                    return method.IsPublic == isPublic ? method
                        : throw new InvalidOperationException("Native accessibility changed: " + method.FullName);
                }

                var update = Require(drive, "UpdateShipTransform", true, @void, input);
                var modes = Require(drive, "UpdateDrive", false, @void, input);
                var force = Require(drive, "ComputeAndApplyForce", false, @void, vec);
                var torque = Require(drive, "ComputeAndApplyTorque", false, @void, vec);
                var accumulateForce = Require(handle, "AddForce", true, @void, "Unity.Mathematics.float3");
                var accumulateTorque = Require(handle, "AddTorque", true, @void, vec);
                var getMode = Require(drive, "get_Mode", true, drive + "/DriveMode");
                var getOrientation = Require(vehicle, "get_Orientation", true, quat);
                var getCore = Require(drive, "get_ComputerSpaceship", false, "VoxelEngine.ComputerSpaceship");
                var enter = Require(vehicle, "Enter", true, @void, player, quat, core);
                var exit = Require(vehicle, "Exit", true, @void, player);
                var shipEnter = Require(ship, "Enter", true, @void, player, quat, core);
                var shipPostExit = Require(ship, "OnPostExit", true, boolean, player);
                var commandPostExit = Require(command, "OnPostExit", true, boolean, player);
                var computerPostExit = Require(core, "OnPostExit", true, boolean, player);
                var vehiclePostExit = Require(vehicle, "OnPostExit", true, boolean, player);
                var commandPump = ContextMethod(module, activator, "OnUpdate", false, @void);
                var setLastShip = Require(player, "set_LastSpaceship", true, @void, parent);
                var setOrientation = Require(vehicle, "set_Orientation", false, @void, quat);
                var playerUpdate = Require(player, "OnUpdate", true, @void);
                const string loop = "Manager.GameloopManager";
                var playerPump = ContextMethod(module, loop, "UpdatePlayerCommands", true, @void);
                var frame = ContextMethod(module, loop, "RunLateUpdateAfterMirror", false, @void);
                var schedule = ContextMethod(module, "Manager.SkyRigidbody", "ScheduleJob", true, @void);

                if (!ContextCalls(update, modes) || !ContextCalls(update, force) || !ContextCalls(update, torque)
                    || !ContextCalls(update, getMode) || !ContextCalls(force, accumulateForce) || !ContextCalls(torque, accumulateTorque)
                    || !ContextCalls(force, getCore) || !ContextCalls(torque, getOrientation)
                    || !Member(getCore, Code.Ldfld, vehicle, "computerBaseVehicle")
                    || !Member(torque, Code.Ldfld, vehicle, "computerBaseVehicle")
                    || !Member(enter, Code.Stfld, vehicle, "computerBaseVehicle") || !ContextCalls(enter, setOrientation)
                    || !ContextCalls(shipEnter, enter) || !ContextCalls(shipEnter, setLastShip)
                    || Member(exit, Code.Stfld, vehicle, "computerBaseVehicle") || ContextCalls(exit, setOrientation)
                    || !ContextCalls(shipPostExit, update) || !ExitPumpRemovesCompleted(commandPump, commandPostExit)
                    || !ContextCalls(computerPostExit, vehiclePostExit) || !computerPostExit.IsVirtual
                    || !shipPostExit.IsVirtual || !ContextCalls(playerUpdate, commandPump)
                    || !Member(playerPump, Code.Ldsfld, player, "AllPlayers") || !ContextCalls(playerPump, playerUpdate)
                    || !CallsInOrder(frame, playerPump, schedule)
                    || !Member(commandPump, Code.Ldfld, activator, "commandToEnter")
                    || !KeyBranch(modes, "boostKeyDown") || !KeyBranch(modes, "lightCruiseKey"))
                    throw new InvalidOperationException("Native sublight drive, retained core or post-exit transition changed.");

                string ListOf(string element) => "System.Collections.Generic.List`1<" + element + ">";
                var activatorType = module.GetType(activator);
                if (!activatorType.Fields.Any(field => field.Name == "commandPostExit" && field.IsPrivate && !field.IsStatic
                        && field.FieldType.FullName == ListOf(command))
                    || !activatorType.Fields.Any(field => field.Name == "commandToEnter" && field.IsPrivate && !field.IsStatic
                        && field.FieldType.FullName == ListOf("VoxelEngine.BrickCoord"))
                    || !Field(module, vehicle, "computerBaseVehicle", core, false)
                    || !Field(module, parent, "controlMode", vehicle + "/VehicleControlMode", false)
                    || !Field(module, ship, "aiController", "Game.IVehicleController", false)
                    || !Field(module, "BlockEntitySync", "instanceData", "EntityInstanceData", false)
                    || !Field(module, "EntityInstanceData", "ownerPlayerNetID", "System.UInt32", false)
                    || !Field(module, "EntityInstanceData", "pilotId", "System.UInt32", false)
                    || !Field(module, player, "AllPlayers", ListOf(player), true)
                    || !Field(module, input, "translation", vec, false) || !Field(module, input, "orient", vec, false))
                    throw new InvalidOperationException("Native lease arbitration, ownership or input fields changed.");

                RequireSurfaceEnum(module, drive + "/DriveMode", "standard", 0);
                RequireSurfaceEnum(module, vehicle + "/VehicleControlMode", "notControlled", 0);
                return new CapabilityResult(id, true, "Validated stock standard drive and unoccupied post-exit arbitration.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(id, false, exception.Message);
            }
        }

        private static bool ExitPumpRemovesCompleted(MethodDefinition pump, MethodDefinition postExit)
        {
            var instructions = pump.Body.Instructions;
            var call = instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Callvirt
                && instruction.Operand is MethodReference target && target.FullName == postExit.FullName);
            var branch = call?.Next;
            if (branch?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S) || branch.Operand is not Instruction skip)
                return false;
            var queue = branch.Next?.Next;
            return queue?.OpCode.Code == Code.Ldfld && queue.Operand is FieldReference field && field.Name == "commandPostExit"
                && queue.Next?.Next is { OpCode.Code: Code.Callvirt, Operand: MethodReference removal }
                && removal.Name == "RemoveAt" && ReferenceEquals(queue.Next.Next.Next, skip);
        }

        private static bool KeyBranch(MethodDefinition method, string name)
            => method.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldfld
                && instruction.Operand is FieldReference field && field.DeclaringType.FullName == "Game.VehicleControlInput"
                && field.Name == name && instruction.Next?.OpCode.Code is (Code.Brfalse or Code.Brfalse_S));

        private static bool CallsInOrder(MethodDefinition caller, MethodDefinition first, MethodDefinition second)
        {
            int Index(MethodDefinition method) => caller.Body.Instructions.IndexOf(caller.Body.Instructions.SingleOrDefault(
                instruction => instruction.OpCode.Code is (Code.Call or Code.Callvirt)
                    && instruction.Operand is MethodReference target && target.FullName == method.FullName));
            int firstIndex = Index(first);
            return firstIndex >= 0 && firstIndex < Index(second);
        }

        private static bool Member(MethodDefinition method, Code opcode, string type, string name)
            => method.Body.Instructions.Any(instruction => instruction.OpCode.Code == opcode
                && instruction.Operand is FieldReference field && field.DeclaringType.FullName == type && field.Name == name);
    }
}
