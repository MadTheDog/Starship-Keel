using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var assembly = AssemblyDefinition.ReadAssembly(args.Single());
var module = assembly.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateWorldSurfaceQuery")
    ?? throw new Exception("Expected a dedicated native world surface audit.");
CapabilityResult Check() => (CapabilityResult)audit.Invoke(null, new object[] { module })!;
void Require(bool expected, string context)
{
    var result = Check();
    if (result.Available != expected) throw new Exception(context + ": " + result.Reason);
}
Require(true, "Stock ray and phase");
module.Mvid = Guid.NewGuid();
Require(true, "An unrelated MVID change");

var ray = module.GetType("VoxelEngine.VoxelRaycast").Methods.Single(method => method.Name == "Raycast" && method.Parameters.Count == 6);
var water = ray.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Stfld
    && instruction.Operand is FieldReference field && field.Name == "includeWater");
var savedOpcode = water.OpCode;
water.OpCode = OpCodes.Nop;
try { Require(false, "Water forwarding removed"); }
finally { water.OpCode = savedOpcode; }

var hit = module.GetType("VoxelEngine.VoxelRaycastHit/Type").Fields.Single(field => field.Name == "water");
var savedConstant = hit.Constant;
hit.Constant = 255;
try { Require(false, "Water enum changed"); }
finally { hit.Constant = savedConstant; }
Console.WriteLine("World surface native contracts passed.");

var editAudit = typeof(ContractAudit).GetMethod("EvaluateTerrainEdits")
    ?? throw new Exception("Expected a dedicated terrain edit audit.");
CapabilityResult EditAudit() => (CapabilityResult)editAudit.Invoke(null, new object[] { module })!;
if (!EditAudit().Available) throw new Exception(EditAudit().Reason);
var nativeEdit = module.GetType("World.Terrain.OctreeNodeResources").Methods.Single(method => method.Name == "UpdateDensitiesOnNode");
var save = nativeEdit.Body.Instructions.Single(instruction => instruction.Operand is MethodReference reference && reference.Name == "QueueNodeSave");
var saveOpcode = save.OpCode;
save.OpCode = OpCodes.Nop;
try
{
    if (EditAudit().Available) throw new Exception("The terrain edit audit admitted a missing native save queue.");
}
finally { save.OpCode = saveOpcode; }

var sampleMath = typeof(ContractAudit).Assembly.GetType("Sevo.Framework.Runtime.TerrainEditMath")
    ?? throw new Exception("Expected terrain brush density math.");
var combine = sampleMath.GetMethod("Combine", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
float Combine(float existing, float boundary, float plane, bool fill)
    => (float)combine.Invoke(null, new object[] { existing, boundary, plane, fill })!;
void Equal(float expected, float actual)
{
    if (actual != expected) throw new Exception($"Expected {expected}, got {actual}.");
}
Equal(1f, Combine(-2f, -2f, 1f, false));
Equal(-1f, Combine(-2f, -2f, -1f, false));
Equal(-2f, Combine(-2f, 1f, 1f, false));
Equal(-1f, Combine(2f, -2f, -1f, true));
Equal(1f, Combine(2f, -2f, 1f, true));
Equal(2f, Combine(2f, 1f, -1f, true));
Console.WriteLine("Terrain edit native contracts and bounded signed density math passed.");
