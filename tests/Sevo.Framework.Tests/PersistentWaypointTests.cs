using Mono.Cecil;
using Sevo.Framework.Runtime;

internal static class PersistentWaypointTests
{
    internal static int Run()
    {
        var profile = Guid.NewGuid();
        using var native = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        var audit = ContractAudit.EvaluatePersistentWaypoints(native);
        if (!audit.Available) throw new InvalidOperationException(audit.Reason);
        var mapAudit = ContractAudit.EvaluatePersistentWaypointMap(native);
        if (!mapAudit.Available) throw new InvalidOperationException(mapAudit.Reason);
        var widget = native.GetType("UI.Galaxy.UI_HolomapEntityWidget");
        var scale = widget.Methods.Single(m => m.Name == "Update").Body.Instructions
            .Single(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_R4 && (float)i.Operand == 4e-7f);
        scale.Operand = 8e-7f;
        if (ContractAudit.EvaluatePersistentWaypointMap(native).Available || !ContractAudit.EvaluatePersistentWaypoints(native).Available)
            throw new InvalidOperationException("A changed Orrery scale must reject map integration while preserving storage.");
        var state = new PersistentWaypointState(profile);
        const string key = "test.survey:landing";
        var first = PersistentWaypoint.AtSystem(key, "Landing", 10, long.MaxValue - 2, 3, 4);
        var second = PersistentWaypoint.AtSystem(key, "Updated", 10, long.MaxValue - 1, 3, 4);
        state.Upsert("One", 1, first);
        state.Upsert("Two", 1, first);
        state.Upsert("One", 1, second);
        state = PersistentWaypointState.Read(state.Serialize(), profile);
        if (state.List("One", 1).Single().SystemX != long.MaxValue - 1
            || state.List("Two", 1).Single().Name != "Landing"
            || state.List("One", 2).Count != 0)
            throw new InvalidOperationException("Waypoint update, world scope or large coordinate was lost.");
        if (!state.Delete("One", 1, key) || state.Delete("One", 1, key))
            throw new InvalidOperationException("Waypoint deletion was not idempotent.");
        state = PersistentWaypointState.Read(state.Serialize(), profile);
        if (state.List("One", 1).Count != 0 || state.List("Two", 1).Count != 1)
            throw new InvalidOperationException("A deleted waypoint returned or another world was modified.");
        try { PersistentWaypointState.Read(state.Serialize(), Guid.NewGuid()); }
        catch (InvalidOperationException) { return 7; }
        throw new InvalidOperationException("Waypoint state crossed managed profiles.");
    }
}
