using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Requests stock recovery for an eligible loaded grid in solo play.</summary>
    public static class GridRecoveryService
    {
        private static int gameThread;

        internal static void Enable() => gameThread = Environment.CurrentManagedThreadId;
        internal static void Disable() => gameThread = 0;

        public static bool TryRecover(ParentEntity grid)
        {
            if (gameThread != Environment.CurrentManagedThreadId || !FunctionalCommandState.IsSolo
                || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle || grid == null)
                return false;
            var manager = BlockEntityManager.instance;
            return manager != null && ReferenceEquals(manager.GetEntityBySGUID(grid.SGUID), grid)
                && grid.TryRecoverFromWreck();
        }
    }

    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateGridRecovery(ModuleDefinition module)
        {
            const string capability = "gridRecovery";
            const string parent = "VoxelEngine.ParentEntity";
            try
            {
                var recover = Method(module, parent, "TryRecoverFromWreck", false, "System.Boolean");
                var eligible = Method(module, parent, "get_CanRecoverFromWreck", false, "System.Boolean");
                var state = Method(module, "PlayerState", "get_sessionMode", true, "PlayerState/SessionMode");
                var lookup = Method(module, "BlockEntityManager", "GetEntityBySGUID", false, parent, "System.String");
                var manager = module.GetType("BlockEntityManager");
                if (recover?.IsPublic != true || eligible?.IsPublic != true || state?.IsPublic != true
                    || lookup?.IsPublic != true || manager == null || !manager.Fields.Any(field =>
                        field.Name == "instance" && field.IsPublic && field.IsStatic && field.FieldType.FullName == manager.FullName)
                    || !RecoveryCalls(recover, "get_isDestroyed") || !RecoveryCalls(recover, "get_CanRecoverFromWreck")
                    || !RecoveryCalls(recover, "get_HullPointPercent") || !RecoveryCalls(recover, "RecoverFromWreckStatus")
                    || !RecoveryCalls(eligible, "get_IsPlayerOwned")
                    || !eligible.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldfld
                        && instruction.Operand is FieldReference field && field.Name == "isWorldWreck"))
                    throw new InvalidOperationException("Native solo, hull or grid ownership recovery gates changed.");
                return new CapabilityResult(capability, true, "Native player grid eligibility and recovery validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool RecoveryCalls(MethodDefinition method, string name) => method.HasBody
            && method.Body.Instructions.Any(instruction => (instruction.OpCode.Code == Code.Call
                || instruction.OpCode.Code == Code.Callvirt) && instruction.Operand is MethodReference target && target.Name == name);
    }
}
