using Mono.Cecil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class NativeBlockVariantAuditTests
{
    internal static void Run(ModuleDefinition module, Action<bool, string> check)
    {
        var method = typeof(ContractAudit).GetMethod("EvaluateNativeBlockVariants");
        check(method != null, "Native command variant audit is present");
        bool Available() => ((CapabilityResult)method!.Invoke(null, new object[] { module })!).Available;
        check(Available(), "Current native command variant contracts are admitted");
        var commandBrick = module.Types.Single(type => type.Name == "CommandBrick`1");
        var arguments = commandBrick.Fields.Single(field => field.Name == "commandArgs");
        var oldName = arguments.Name;
        arguments.Name += "_changed";
        try { check(!Available(), "Changed native argument storage disables variant registration"); }
        finally { arguments.Name = oldName; }
    }
}
