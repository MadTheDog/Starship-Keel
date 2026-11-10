using System.Numerics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

internal static class ConnectedRailTests
{
    internal static int Run()
    {
        var path = MechanismPath.FromConnectedRail(new[] { Vector3.Zero, Vector3.UnitX * 2,
            Vector3.UnitX * 5, Vector3.UnitX * 5 + Vector3.UnitZ * 3 });
        if (path.Length != 8 || !path.TryGetOffset(6, out var position)
            || position != Vector3.UnitX * 5 + Vector3.UnitZ
            || path.LimitStep(4, 8, 4) != 1 || path.LimitStep(6, 0, 4) != 1)
            throw new InvalidOperationException("Rail travel did not stop at a corner or restore a distance along the correct leg.");
        Reject(new[] { Vector3.Zero, Vector3.UnitX, new Vector3(2, 1, 0) });
        Reject(new[] { Vector3.Zero, Vector3.UnitX * 2, Vector3.UnitX });
        if (!path.TryGetRailDisplacement(0, 2, out var home) || home != Vector3.Zero
            || !path.TryGetRailDisplacement(-2, 2, out var negativeEnd) || negativeEnd != -Vector3.UnitX * 2
            || !path.TryGetRailDisplacement(4, 2, out var corner) || corner != Vector3.UnitX * 3 + Vector3.UnitZ
            || path.TryGetRailDisplacement(-3, 2, out _))
            throw new InvalidOperationException("Stock extension distance did not preserve the native home position.");
        using var native = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        var audit = ContractAudit.EvaluateConnectedRails(native);
        if (!audit.Available) throw new InvalidOperationException(audit.Reason);
        var tools = ContractAudit.EvaluateStockRailTools(native);
        if (!tools.Available) throw new InvalidOperationException(tools.Reason);
        var links = native.GetType("AddonBrick").Methods.Single(method => method.Name == "OnLinkedToCommand");
        var publish = links.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Stfld
            && instruction.Operand is FieldReference field && field.Name == "stackAddonCommand");
        publish.OpCode = OpCodes.Nop;
        if (ContractAudit.EvaluateStockRailTools(native).Available || !ContractAudit.EvaluateConnectedRails(native).Available)
            throw new InvalidOperationException("Native add-on publication drift was not isolated to built-in rail tools.");
        publish.OpCode = OpCodes.Stfld;
        var capsule = native.GetType("Manager.SkyPhysics/QueryOverlapCapsuleCandidates");
        var terrain = capsule.Methods.Single(method => method.Name == "OnElementIntersect"
            && method.Parameters[0].ParameterType.Name == "TrianglesElement");
        terrain.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "Add").OpCode = OpCodes.Nop;
        if (ContractAudit.EvaluateStockRailTools(native).Available || !ContractAudit.EvaluateConnectedRails(native).Available)
            throw new InvalidOperationException("Native broadphase drift was not isolated to built-in rail tools.");
        var apply = native.GetType("VoxelEngine.SliderSystem").Methods.Single(method => method.Name == "ApplyTransform");
        apply.Body.Instructions.First(instruction => instruction.OpCode.Code == Code.Sub).OpCode = OpCodes.Add;
        if (ContractAudit.EvaluateConnectedRails(native).Available || !ContractAudit.EvaluateMechanismPaths(native).Available)
            throw new InvalidOperationException("Native extension drift was not isolated to connected rail travel.");
        return 9;
    }

    private static void Reject(Vector3[] points)
    {
        try { MechanismPath.FromConnectedRail(points); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An unsupported rail junction was accepted.");
    }
}
