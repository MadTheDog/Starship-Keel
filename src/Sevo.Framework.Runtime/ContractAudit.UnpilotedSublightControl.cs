using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateUnpilotedSublightControl(ModuleDefinition module)
        {
            const string name = "unpilotedSublightControl";
            const string drive = "SpaceshipDrive", ship = "Spaceship", vehicle = "BaseVehicle";
            const string systems = "VoxelEngine.EntitySystems", body = "VoxelEngine.EntityRigidbody";
            const string computer = "VoxelEngine.ComputerBaseVehicle", handle = "Manager.SkyRigidbody/Handle";
            const string vec = "UnityEngine.Vector3", quat = "UnityEngine.Quaternion", none = "System.Void";
            const string single = "System.Single", boolean = "System.Boolean";
            try
            {
                if (!EvaluateSublightControl(module).Available)
                    throw new InvalidOperationException("The stock sublight drive contract is unavailable.");

                var constructor = ContextMethod(module, drive, ".ctor", false, none, ship, body);
                var force = ContextMethod(module, drive, "ComputeAndApplyForce", false, none, vec);
                var torque = ContextMethod(module, drive, "ComputeAndApplyTorque", false, none, vec);
                var thrust = ContextMethod(module, systems, "ComputeThrust", false, none, vec, boolean, boolean);
                var updateThrusters = ContextMethod(module, systems, "UpdateThrusters", false, none);
                var accumulate = ContextMethod(module, systems, "UpdateThrust", false, none, vec, vec, vec);
                var orientation = ContextMethod(module, vehicle, "get_Orientation", false, quat);
                var setOrientation = ContextMethod(module, vehicle, "set_Orientation", false, none, quat);
                var forward = ContextMethod(module, systems, "get_ThrustForward", false, single);
                var setForward = ContextMethod(module, systems, "set_ThrustForward", false, none, single);
                var lateral = ContextMethod(module, systems, "get_ThrustLateral", false, single);
                var setLateral = ContextMethod(module, systems, "set_ThrustLateral", false, none, single);
                var linkYoke = ContextMethod(module, computer, "OnPredecessorAdded", false, none,
                    "VoxelEngine.ICommand", boolean, boolean);
                var setYokeOrientation = ContextMethod(module, computer, "set_inputOrientation", false, none, quat);

                if (!constructor.IsPublic || !force.IsPrivate || !torque.IsPrivate || !thrust.IsPublic
                    || !SublightBackingField(orientation, setOrientation, vehicle, "<Orientation>k__BackingField", quat)
                    || !SublightBackingField(forward, setForward, systems, "<ThrustForward>k__BackingField", single)
                    || !SublightBackingField(lateral, setLateral, systems, "<ThrustLateral>k__BackingField", single)
                    || !ContextCalls(linkYoke, setYokeOrientation)
                    || !module.GetType(drive).Fields.Any(field => field.Name == "q" && field.FieldType.FullName == quat
                        && field.IsPrivate && !field.IsStatic)
                    || !Member(force, Code.Ldfld, drive, "q")
                    || !ContextCalls(force, forward) || !ContextCalls(force, lateral) || !ContextCalls(torque, orientation)
                    || !Member(force, Code.Ldflda, drive, "skyRigidbody") || !Member(torque, Code.Ldflda, drive, "skyRigidbody"))
                    throw new InvalidOperationException("Stock flight orientation, drive or thrust totals changed.");

                var localControl = ContextMethod(module, vehicle, "get_IsControlledByLocalPlayer", false, boolean);
                var controlCheck = localControl.Body.Instructions;
                RequireSurfaceEnum(module, vehicle + "/VehicleControlMode", "localPlayer", 1);
                if (controlCheck.Count != 6 || controlCheck[0].OpCode.Code != Code.Ldarg_0
                    || controlCheck[1].Operand is not FieldReference entity || entity.DeclaringType.FullName != vehicle
                    || entity.Name != "entity" || entity.FieldType.FullName != "VoxelEngine.ParentEntity"
                    || controlCheck[2].Operand is not FieldReference controlMode || controlMode.DeclaringType.FullName != entity.FieldType.FullName
                    || controlMode.Name != "controlMode" || controlCheck[1].OpCode.Code != Code.Ldfld || controlCheck[2].OpCode.Code != Code.Ldfld
                    || controlCheck[3].OpCode.Code != Code.Ldc_I4_1 || controlCheck[4].OpCode.Code != Code.Ceq || controlCheck[5].OpCode.Code != Code.Ret
                    || !SublightScopedDriveMethod(force, localControl) || !SublightScopedDriveMethod(torque, localControl))
                    throw new InvalidOperationException("Stock force and torque no longer isolate local cockpit effects.");

                var handleStore = constructor.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Stfld
                    && instruction.Operand is FieldReference field && field.DeclaringType.FullName == drive
                    && field.Name == "skyRigidbody" && field.FieldType.FullName == handle);
                if (handleStore?.Previous?.Operand is not MethodReference getHandle || getHandle.DeclaringType.FullName != body
                    || getHandle.Name != "get_OwnSkyRigidbody" || getHandle.ReturnType.FullName != handle
                    || handleStore.Previous.Previous?.OpCode.Code != Code.Ldarg_2
                    || constructor.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stsfld
                        || (instruction.OpCode.Code == Code.Stfld && instruction.Operand is FieldReference field && field.DeclaringType.FullName != drive))
                    || !OnlySublightCalls(constructor, call => (call.Name == ".ctor" && (call.DeclaringType.FullName is
                        "Common.IntEventVariable" or "Common.PID_Controller" or "System.Object" or "System.Action"))
                        || (call.DeclaringType.FullName == vec && call.Name == "get_zero")
                        || (call.DeclaringType.FullName == "UnityEngine.Component" && call.Name == "get_transform")
                        || call.FullName == getHandle.FullName
                        || (call.DeclaringType.FullName == "Common.GenericEventVariable`1<System.Int32>" && call.Name == "add_OnValueChanged")))
                    throw new InvalidOperationException("Stock lease-owned drive construction changed.");

                var refresh = thrust.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Call
                    && instruction.Operand is MethodReference method && method.FullName == updateThrusters.FullName);
                var skip = refresh?.Previous?.Previous;
                if (skip?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S) || skip.Previous?.OpCode.Code != Code.Ldarg_3
                    || !ReferenceEquals(skip.Operand, refresh!.Next)
                    || !ContextCalls(thrust, setForward) || !ContextCalls(thrust, setLateral)
                    || !ContextCalls(accumulate, setForward) || !ContextCalls(accumulate, setLateral)
                    || !OnlySublightCalls(thrust, call => call.FullName == updateThrusters.FullName || call.FullName == accumulate.FullName
                        || call.FullName == setForward.FullName || call.FullName == setLateral.FullName || call.FullName == lateral.FullName
                        || (call.DeclaringType.FullName is "VoxelEngine.SystemThruster" or "VoxelEngine.SystemLightcruiseEngine"
                            && call.Name is "get_ForwardThrust" or "get_LateralThrust")
                        || (call.DeclaringType.FullName == "VoxelEngine.FlowBalance" && call.Name == "get_maxStock")
                        || (call.DeclaringType.FullName.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal)
                            && call.Name is "GetEnumerator" or "MoveNext" or "get_Current")
                        || (call.DeclaringType.FullName == "System.IDisposable" && call.Name == "Dispose"))
                    || !OnlySublightCalls(accumulate, call => call.FullName == forward.FullName || call.FullName == lateral.FullName
                        || call.FullName == setForward.FullName || call.FullName == setLateral.FullName
                        || (call.DeclaringType.FullName == vec && call.Name is "Dot" or "get_magnitude")
                        || (call.DeclaringType.FullName == "UnityEngine.Mathf" && call.Name == "Max"))
                    || thrust.Body.Instructions.Concat(accumulate.Body.Instructions).Any(instruction => instruction.OpCode.Code is Code.Stfld or Code.Stsfld))
                    throw new InvalidOperationException("Stock thrust computation can no longer be scoped to the two saved totals.");

                return new CapabilityResult(name, true, "Validated lease-owned stock drive and temporary flight inputs for unoccupied ships.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(name, false, exception.Message);
            }
        }

        private static bool SublightBackingField(MethodDefinition getter, MethodDefinition setter, string type, string name, string valueType)
        {
            var read = getter.Body.Instructions;
            var write = setter.Body.Instructions;
            return getter.IsPublic && setter.IsPrivate && read.Count == 3 && write.Count == 4
                && read[0].OpCode.Code == Code.Ldarg_0 && read[1].OpCode.Code == Code.Ldfld && read[2].OpCode.Code == Code.Ret
                && write[0].OpCode.Code == Code.Ldarg_0 && write[1].OpCode.Code == Code.Ldarg_1
                && write[2].OpCode.Code == Code.Stfld && write[3].OpCode.Code == Code.Ret
                && read[1].Operand is FieldReference field && field.DeclaringType.FullName == type
                && field.Name == name && field.FieldType.FullName == valueType
                && write[2].Operand is FieldReference stored && stored.FullName == field.FullName;
        }

        private static bool OnlySublightCalls(MethodDefinition method, Func<MethodReference, bool> allowed)
            => method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                .All(instruction => instruction.Operand is MethodReference call && allowed(call));

        private static bool SublightScopedDriveMethod(MethodDefinition method, MethodDefinition localControl)
        {
            var instructions = method.Body.Instructions;
            var hint = instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Call
                && instruction.Operand is MethodReference call && call.DeclaringType.FullName == "UI.UI_HelpMessage" && call.Name == "Submit");
            var branch = hint?.Previous?.Previous;
            return hint?.Previous?.OpCode.Code == Code.Ldsflda && branch?.OpCode.Code is (Code.Brfalse or Code.Brfalse_S)
                && ReferenceEquals(branch.Operand, hint.Next) && hint.Next?.OpCode.Code == Code.Ret
                && branch.Previous?.OpCode.Code == Code.Callvirt && branch.Previous.Operand is MethodReference test
                && test.FullName == localControl.FullName
                && !instructions.Any(instruction => instruction.OpCode.Code is Code.Stsfld or Code.Stobj or Code.Cpobj
                    || (instruction.OpCode.Code == Code.Stfld && instruction.Operand is FieldReference field
                        && field.DeclaringType.FullName != "UnityEngine.Vector3"))
                && OnlySublightCalls(method, call => call.DeclaringType.FullName switch
                {
                    "SpaceshipDrive" => call.Name is "get_orientInv" or "get_ComputerSpaceship",
                    "BaseVehicle" => call.Name is "get_IsControlledByLocalPlayer" or "get_Orientation",
                    "Manager.SkyRigidbody/Handle" => call.Name is "get_Data" or "AddForce" or "AddTorque",
                    "VoxelEngine.ComputerBaseVehicle" => call.Name is "get_LinearVelocityTauDFactor" or "get_MaximumVelocity"
                        or "get_AgilityRating" or "get_AngularSpeed" or "GetThrustRatio",
                    "VoxelEngine.EntityRigidbody" => call.Name == "get_Weight",
                    "VoxelEngine.EntitySystems" => call.Name is "get_ThrustForward" or "get_ThrustLateral",
                    "VoxelEngine.ParentEntity" => call.Name is "get_Rigidbody" or "get_Systems",
                    "UnityEngine.Transform" => call.Name == "get_rotation",
                    "UnityEngine.Time" => call.Name == "get_deltaTime",
                    "UnityEngine.Mathf" => call.Name is "Clamp" or "Min",
                    "UnityEngine.Quaternion" => call.Name is "Inverse" or "op_Multiply",
                    "UnityEngine.Vector3" => call.Name is "op_Implicit" or "op_Subtraction" or "op_Division" or "op_Multiply" or "Scale",
                    "Vector3Extensions" => call.Name is "ClampComponents" or "FromFloat3",
                    "Common.PID_Controller" => call.Name == "GetFeedback",
                    "UI.UI_HelpMessage" => call.Name == "Submit",
                    _ => false
                });
        }
    }
}
