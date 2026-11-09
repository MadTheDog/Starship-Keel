using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var assembly = AssemblyDefinition.ReadAssembly(args.Single());
var module = assembly.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateLogicOutputs")
    ?? throw new Exception("Expected a dedicated native logic output audit.");
CapabilityResult Result() => (CapabilityResult)audit.Invoke(null, new object[] { module })!;
void Require(bool available, string context)
{
    var result = Result();
    if (result.Available != available) throw new Exception(context + ": " + result.Reason);
}
Require(true, "Stock event binding tables");
module.Mvid = Guid.NewGuid();
Require(true, "Unrelated assembly identity");

var nativeEvent = module.GetType("VoxelEngine.EventId").Fields.Single(field => field.Name == "none");
var value = nativeEvent.Constant;
nativeEvent.Constant = 125;
try { Require(false, "A native event conflicts with a stable Keel event slot"); }
finally { nativeEvent.Constant = value; }

var resource = module.GetType("VoxelEngine.CommandEventGateResources");
var initializer = resource.Methods.Single(method => method.Name == ".cctor");
var publish = initializer.Body.Instructions.Single(instruction => instruction.Operand is FieldReference field
    && field.Name == "FieldInfos" && instruction.OpCode.Code == Code.Stsfld);
var size = publish.Previous.Previous;
var sizeOpcode = size.OpCode;
var sizeOperand = size.Operand;
size.OpCode = OpCodes.Ldc_I4;
size.Operand = 126;
try { Require(false, "A native event table shrank below the reserved slots"); }
finally
{
    size.OpCode = sizeOpcode;
    size.Operand = sizeOperand;
}

var gate = module.GetType("VoxelEngine.CommandEventGate").Methods.Single(method => method.Name == "SetStateData");
var reads = gate.Body.Instructions.Where(instruction => instruction.Operand is FieldReference field
    && field.Name == "FieldInfos" && instruction.OpCode.Code == Code.Ldsfld).ToArray();
if (reads.Length == 0) throw new Exception("Expected native Event Gate field reads.");
foreach (var instruction in reads) instruction.OpCode = OpCodes.Nop;
try { Require(false, "The gate stopped reading the native field table"); }
finally { foreach (var instruction in reads) instruction.OpCode = OpCodes.Ldsfld; }
var releaseTargets = module.GetType("VoxelEngine.CommandCommon").Methods.Single(method => method.Name == "ReleaseTargets");
var notifyRemoved = releaseTargets.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
    && method.Name == "OnTargetRemoved");
var notifyOpcode = notifyRemoved.OpCode;
notifyRemoved.OpCode = OpCodes.Nop;
try { Require(false, "Native source deletion no longer notifies detached targets"); }
finally { notifyRemoved.OpCode = notifyOpcode; }

var jobData = module.GetType("VoxelEngine.CommandEventGateResources/EventGateJobData");
var sourceSlot = jobData.Fields.Single(field => field.Name == "stateDataId");
sourceSlot.Name = "movedStateSlot";
try { Require(false, "The detached Event Gate job source slot changed"); }
finally { sourceSlot.Name = "stateDataId"; }
Console.WriteLine("Logic output identity and native Event Gate contracts passed.");
