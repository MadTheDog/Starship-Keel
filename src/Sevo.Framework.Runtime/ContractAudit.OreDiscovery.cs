using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the accepted-pulse seam separately from native scanner widget publication.</summary>
        public static CapabilityResult EvaluateOreDiscovery(ModuleDefinition module)
        {
            const string capability = "oreDiscovery";
            const string scanner = "World.Terrain.OreScannerResources";
            const string state = "PlayerState/SessionMode";
            try
            {
                var press = Method(module, scanner, "OnScannerKeyPressed", true, "System.Void");
                var reload = Method(module, scanner, "OnGameReload", true, "System.Void");
                var range = module.GetType(scanner)?.Fields.SingleOrDefault(field => field.Name == "PLAYER_SCANNER_RANGE");
                if (press?.IsPublic != true || !press.HasBody || reload?.IsPublic != true
                    || Method(module, scanner, "get_IsScannerRunning", true, "System.Boolean")?.IsPublic != true
                    || !Field(module, scanner, "lastActivateTime", "System.Single", true)
                    || range?.IsLiteral != true || !Equals(range.Constant, 300f)
                    || !reload.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Manager.OnGameReloadCallback"))
                    throw new InvalidOperationException("Stock scanner activation, range, or reload contract changed.");
                var writes = press.Body.Instructions.Where(instruction =>
                    instruction.OpCode.Code == Code.Stsfld && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == scanner && field.Name == "lastActivateTime").ToArray();
                if (writes.Length != 1 || !(writes[0].Previous?.Operand is MethodReference time)
                    || time.DeclaringType.FullName != "UnityEngine.Time" || time.Name != "get_timeSinceLevelLoad"
                    || !press.Body.Instructions.TakeWhile(instruction => instruction != writes[0])
                        .Any(instruction => instruction.OpCode.Code == Code.Ret))
                    throw new InvalidOperationException("Accepted scanner pulse no longer has a separate timestamp write.");
                if (!Field(module, "Manager.Dependency", "PlayerCameraComponent", "UnityEngine.Camera", true)
                    || Method(module, "PositionManager", "ToSystemPosition", true, "Vector3l", "UnityEngine.Vector3")?.IsPublic != true
                    || Method(module, "Vector3l", "ToDouble3", false, "Unity.Mathematics.double3")?.IsPublic != true
                    || !Field(module, "World.PlanetarySystem", "CurrentStarData", "World.StarData", true)
                    || !Field(module, "World.PlanetarySystem", "InSystemTransition", "System.Boolean", true)
                    || !Field(module, "World.PlanetarySystem", "OnNewSystem", "System.Action", true)
                    || Method(module, "PlayerState", "get_sessionMode", true, state)?.IsPublic != true
                    || !Equals(module.GetType(state)?.Fields.SingleOrDefault(field => field.IsLiteral && field.Name == "solo")?.Constant, 1))
                    throw new InvalidOperationException("Stock scanner context or coordinates changed.");
                return new CapabilityResult(capability, true, "Accepted stock scanner pulse and system coordinates validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
