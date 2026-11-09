using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateVehicleControlProvider(ModuleDefinition module)
        {
            const string capability = "vehicleControlProvider";
            const string pilot = "Game.SpaceshipAutoPilot";
            const string input = "Game.VehicleControlInput";
            try
            {
                var control = NavMethod(module, "Spaceship", "Control", false, "System.Void", "Game.PlayerCommandManager");
                var manual = NavMethod(module, input, "FromPlayerInput", true, input);
                var drive = NavMethod(module, "SpaceshipDrive", "UpdateShipTransform", false, "System.Void", input);
                var instructions = control.Body.Instructions;
                var manualCalls = instructions.Where(instruction => CallsVehicle(instruction, manual)).ToArray();
                var autopilotCalls = instructions.Where(instruction => CallsVehicle(instruction, pilot, "GetControlInput")).ToArray();
                var driveCalls = instructions.Where(instruction => CallsVehicle(instruction, drive)).ToArray();
                if (manualCalls.Length != 1 || autopilotCalls.Length != 1 || driveCalls.Length != 1)
                    throw new InvalidOperationException("Native spaceship control no longer has one input call per stock branch and one drive call.");
                int manualIndex = instructions.IndexOf(manualCalls[0]);
                int autoIndex = instructions.IndexOf(autopilotCalls[0]);
                int driveIndex = instructions.IndexOf(driveCalls[0]);
                if (!control.IsPublic || !manual.IsPublic || control.Body.HasExceptionHandlers
                    || autoIndex >= manualIndex || manualIndex >= driveIndex
                    || autopilotCalls[0].Next?.Next is not { Operand: Instruction stockMerge } skipManual
                    || skipManual.OpCode.Code is not (Code.Br or Code.Br_S)
                    || instructions.IndexOf(stockMerge) <= manualIndex || instructions.IndexOf(stockMerge) > driveIndex
                    || !instructions.Skip(autoIndex).Take(manualIndex - autoIndex).Any(instruction =>
                        CallsVehicle(instruction, "BaseVehicle", "get_IsControlledByLocalPlayer"))
                    || !instructions.Skip(autoIndex).Take(manualIndex - autoIndex).Any(instruction =>
                        CallsVehicle(instruction, "PlayerState", "get_InMenu"))
                    || !instructions.Skip(autoIndex).Take(manualIndex - autoIndex).Any(instruction =>
                        CallsVehicle(instruction, "Game.PlayerPhotoMode", "get_IsInFreeCamera")))
                    throw new InvalidOperationException("Stock autopilot priority or local manual input routing changed.");

                return new CapabilityResult(capability, true, "One local manual input call after the stock autopilot branch was validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateWaypointFlight(ModuleDefinition module)
        {
            const string capability = "waypointFlight";
            const string pilot = "Game.SpaceshipAutoPilot";
            const string ship = "Spaceship";
            const string tracking = "UI.TrackingData";
            const string player = "Game.PlayerCommandManager";
            try
            {
                var control = NavMethod(module, ship, "Control", false, "System.Void", player);
                var exit = NavMethod(module, ship, "OnPostExit", false, "System.Boolean", player);
                var engage = NavMethod(module, pilot, "EngageAutoFlight", false, "System.Void", tracking);
                var cancel = NavMethod(module, pilot, "CancelCurrentMode", false, "System.Void");
                var reset = NavMethod(module, pilot, "Reset", false, "System.Void");
                var advance = NavMethod(module, pilot, "GetControlAutoFlight", false, "Game.VehicleControlInput");
                var distance = NavMethod(module, "VoxelEngine.ParentEntity", "DistanceFromRadius", false, "System.Single", tracking);
                var mode = NavMethod(module, pilot, "get_mode", false, pilot + "/Mode");
                var stockMode = module.GetType(pilot + "/Mode")?.Fields.SingleOrDefault(field => field.Name == "autoFlight");
                if (!control.IsPublic || !exit.IsPublic || !engage.IsPublic || !cancel.IsPublic || !distance.IsPublic || !mode.IsPublic
                    || stockMode?.HasConstant != true || Convert.ToInt32(stockMode.Constant) != (int)Game.SpaceshipAutoPilot.Mode.autoFlight
                    || !NavCalls(control, "CheckForAutopilotActive", pilot) || !NavCalls(control, "GetControlInput", pilot)
                    || !NavCalls(cancel, reset.Name, pilot) || !NavCalls(engage, "set_mode", pilot)
                    || !NavCalls(advance, "get_WorldPosition", tracking) || !NavCalls(distance, "get_SystemPosition", tracking)
                    || !engage.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stfld
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == pilot
                        && field.Name == "trackingData" && field.FieldType.FullName == tracking))
                    throw new InvalidOperationException("Native waypoint engagement, approach, or cancellation changed.");

                return new CapabilityResult(capability, true, "Local native approach and cancellation contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool CallsVehicle(Instruction instruction, MethodDefinition target)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference called
                && called.FullName == target.FullName;

        private static bool CallsVehicle(Instruction instruction, string owner, string name)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference called
                && called.DeclaringType.FullName == owner && called.Name == name;
    }
}
