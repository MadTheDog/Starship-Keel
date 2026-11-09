using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateGridPhysics(ModuleDefinition module)
        {
            const string capability = "gridPhysics";
            const string loop = "Manager.GameloopManager";
            const string sky = "Manager.SkyRigidbody";
            const string data = sky + "/Data";
            const string handle = sky + "/Handle";
            const string entity = "VoxelEngine.ParentEntity";
            const string entityBody = "VoxelEngine.EntityRigidbody";
            const string flag = "System.Boolean";
            const string integer = "System.Int32";
            const string nothing = "System.Void";
            try
            {
                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters)
                    => Method(module, type, name, isStatic, result, parameters) is { IsPublic: true } method ? method
                        : throw new InvalidOperationException("Missing native grid physics method: " + type + "." + name);

                var frame = Method(module, loop, "RunLateUpdateAfterMirror", false, nothing)
                    ?? throw new InvalidOperationException("The native rigidbody phase caller is unavailable.");
                var schedule = Require(sky, "ScheduleJob", true, nothing);
                var complete = Require(sky, "CompleteJob", true, nothing);
                var dispose = Require(sky, "DisposeJobResources", true, nothing);
                int CallIndex(MethodDefinition caller, MethodDefinition target)
                {
                    if (!caller.HasBody) return -1;
                    var calls = caller.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                        && instruction.Operand is MethodReference reference && reference.FullName == target.FullName).ToArray();
                    return calls.Length == 1 ? caller.Body.Instructions.IndexOf(calls[0]) : -1;
                }

                int before = CallIndex(frame, schedule);
                int after = CallIndex(frame, complete);
                if (before < 0 || before >= after || after >= CallIndex(frame, dispose)
                    || !schedule.Body.Instructions.Any(instruction =>
                        instruction.OpCode.Code == Code.Call && instruction.Operand is MethodReference reference
                        && reference.DeclaringType.FullName == "UnityEngine.Time" && reference.Name == "get_deltaTime"
                        && reference.ReturnType.FullName == "System.Single"))
                    throw new InvalidOperationException("The native rigidbody schedule/complete/dispose or simulation time contract changed.");

                Require(loop, "get_IsMainThread", true, flag);
                Require(loop, "get_IsNativeContainerAccessUnsafe", true, flag);
                Require("PlayerState", "get_sessionMode", true, "PlayerState/SessionMode");
                Require("PlayerState", "get_InLoadingScreen", true, flag);
                Require("PlayerState", "get_IsQuittingToDesktopOrTitle", true, flag);
                Require("PlayerState", "get_InMenu", true, flag);
                RequireSurfaceEnum(module, "PlayerState/SessionMode", "solo", (int)PlayerState.SessionMode.solo);
                Require(entity, "get_Rigidbody", false, entityBody);
                Require(entityBody, "get_OwnSkyRigidbody", false, handle);
                Require(entityBody, "get_isParented", false, flag);
                Require(handle, "get_id", false, integer);
                Require(handle, "get_IsValid", false, flag);
                var read = Require(handle, "get_Data", false, data);
                var write = Require(handle, "set_Data", false, nothing, data);
                var force = Require(handle, "AddForceAtSystemPosition", false, nothing, "Unity.Mathematics.float3", "Vector3l");
                var transform = Require("Manager.SkyTransformData", "LocalToSystem", false, "Vector3l", "UnityEngine.Vector3");
                var instructions = force.Body.Instructions;
                const string forceVector = "Unity.Mathematics.float3";
                int AddressIndex(string name) => instructions.IndexOf(instructions.FirstOrDefault(instruction =>
                    SurfaceField(instruction, Code.Ldflda, data, name, forceVector)));
                int forceAddress = AddressIndex("force");
                int torqueAddress = AddressIndex("torque");
                int writeIndex = CallIndex(force, write);
                bool StoresBetween(int begin, int end) => begin >= 0 && end > begin
                    && instructions.Skip(begin + 1).Take(end - begin - 1).Any(instruction =>
                        instruction.OpCode.Code == Code.Stobj && instruction.Operand is TypeReference type
                        && type.FullName == forceVector);
                if (!transform.HasBody || CallIndex(force, read) < 0 || CallIndex(force, read) >= forceAddress
                    || !StoresBetween(forceAddress, torqueAddress) || !StoresBetween(torqueAddress, writeIndex))
                    throw new InvalidOperationException("Native force application no longer writes force and torque through the handle.");

                var dataType = module.GetType(data);
                bool InternalFlag(string name) => dataType?.Fields.Any(field => field.Name == name && !field.IsStatic
                    && field.FieldType.FullName == flag) == true;
                if (!InternalFlag("hasLocalAuthority") || !InternalFlag("isKinematic")
                    || !Field(module, data, "isForcedStatic", flag, false) || !Field(module, data, "forceSleeping", flag, false)
                    || !Field(module, data, "mass", "System.Single", false) || !Field(module, data, "transformId", integer, false)
                    || !Field(module, sky, "Entities", "System.Collections.Generic.List`1<" + entity + ">", true))
                    throw new InvalidOperationException("Native rigidbody lifetime or authority fields changed.");

                return new CapabilityResult(capability, true, "Validated pre-simulation rigidbody phase and native force accumulation.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
