using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class GridDestructionTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(reason);
            checks++;
        }

        var observers = new GridDestructionObservers();
        var notice = new GridDestructionNotice("grid-sguid", "Fixture", 93, wasNpc: true, wasGeneratedWreck: false);
        var calls = new List<string>();
        var reports = new List<string>();
        observers.Add("zeta.mod", "recovery", received =>
        {
            Check(received.GridKey == "grid-sguid" && received.GridName == "Fixture" && received.StarSeed == 93
                && received.WasNpc && !received.WasGeneratedWreck, "Snapshot fields were changed during notification.");
            calls.Add("recovery");
        });
        observers.Add("alpha.mod", "telemetry", _ =>
        {
            calls.Add("telemetry");
            throw new InvalidOperationException("Fixture observer failed.");
        });
        bool duplicateRejected = false;
        try { observers.Add("zeta.mod", "recovery", _ => { }); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        Check(duplicateRejected, "Duplicate observer registration was accepted.");
        observers.PublishTransition(null, true, reports.Add);
        observers.PublishTransition(notice, false, reports.Add);
        Check(calls.Count == 0 && reports.Count == 0, "An unconfirmed transition notified an observer.");
        observers.PublishTransition(notice, true, reports.Add);
        Check(calls.SequenceEqual(new[] { "telemetry", "recovery" }), "Observer ordering or failure isolation changed.");
        Check(reports.Single().Contains("alpha.mod:telemetry"), "Observer diagnostic omitted the failing key.");
        observers.PublishTransition(notice, true, _ => throw new InvalidOperationException("Fixture logger failed."));
        Check(calls.Count == 4, "Diagnostic failure interrupted later observers.");

        using var assembly = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = assembly.MainModule;
        void Audit(bool expected)
        {
            var result = ContractAudit.EvaluateGridDestruction(module);
            Check(result.Name == "gridDestruction" && result.Available == expected, result.Reason);
        }
        Audit(true);
        module.Mvid = Guid.NewGuid();
        Audit(true);
        var target = module.GetType("VoxelEngine.ParentEntity").Methods.Single(method => method.Name == "OnGameplayDestructionServer");
        var gate = target.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Brtrue_S);
        gate.OpCode = OpCodes.Brfalse_S;
        Audit(false);
        gate.OpCode = OpCodes.Brtrue_S;
        var server = module.GetType("Player_NetworkSetup").Methods.Single(method => method.Name == "ServerDestroyEntity");
        var stateSetter = module.GetType("VoxelEngine.EntitySystems").Methods.Single(method => method.Name == "set_IsDestroyed");
        var storedState = stateSetter.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld).Previous;
        storedState.OpCode = OpCodes.Ldc_I4_0;
        Audit(false);
        storedState.OpCode = OpCodes.Ldarg_1;
        var serverGate = server.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Brtrue);
        serverGate.OpCode = OpCodes.Brfalse;
        Audit(false);
        serverGate.OpCode = OpCodes.Brtrue;
        var authoritativeCall = server.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "OnGameplayDestructionServer");
        authoritativeCall.OpCode = OpCodes.Nop;
        Audit(false);
        return checks;
    }
}
