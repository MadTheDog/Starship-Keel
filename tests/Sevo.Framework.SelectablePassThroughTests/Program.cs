using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateSelectablePassThrough")
    ?? throw new Exception("Missing selectable pass-through native audit.");

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

bool Available()
{
    var result = (CapabilityResult)audit.Invoke(null, new object[] { module })!;
    Console.WriteLine(result.Reason);
    return result.Available;
}

Check(Available(), "The stock contact, targeting and placement paths should be admitted.");
Guid originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try { Check(Available(), "Unrelated assembly identity must not gate this capability."); }
finally { module.Mvid = originalMvid; }

void Mutate(string type, string method, string member, Action<Instruction>? mutation = null,
    Func<Instruction, Instruction>? select = null)
{
    var instruction = module.GetType(type).Methods.Where(candidate => candidate.Name == method && candidate.HasBody)
        .SelectMany(candidate => candidate.Body.Instructions)
        .First(candidate => candidate.Operand is MemberReference reference && reference.Name == member);
    if (select != null) instruction = select(instruction);
    var opcode = instruction.OpCode;
    var operand = instruction.Operand;
    try
    {
        if (mutation == null) instruction.OpCode = OpCodes.Nop;
        else mutation(instruction);
        Check(!Available(), $"Changed {method} → {member} must disable selectable pass-through.");
    }
    finally { instruction.OpCode = opcode; instruction.Operand = operand; }
}

Mutate("VoxelEngine.BrickCollider", "SetBrick", "IsCollisionOff");
Mutate("VoxelEngine.BrickCollider", "AddColliderData", "skipCollision");
Mutate("VoxelEngine.BrickProperties", ".ctor", "isCollisionOff");
Mutate("VoxelEngine.Brick", "LoadResourcesRuntime", "BrickProperties");
Mutate("VoxelEngine.BrickCollider/Data", "PenetrationPointProjected", "skipCollision");
Mutate("Manager.SkyPhysics/QueryRaycastOnEntity", "OnElementIntersect", "isCollisionOff");
Mutate("VoxelEngine.CollisionPointsBVHResources/ComputePointsJob", "SearchForDepth", "Execute");
Mutate("Manager.SkyPhysics/QueryOverlapCapsuleCandidates", "OnElementIntersect", "isCollisionOff");
Mutate("Manager.SkyPhysics/QueryRaycast", "OnElementIntersect", "Raycast");
Mutate("Manager.SkyPhysics/QueryRaycast", "OnElementIntersect", "isDecal",
    instruction => instruction.Operand = module.GetType("VoxelEngine.BrickProperties").Fields.Single(field => field.Name == "isCollisionOff"));
Mutate("VoxelEngine.BrickCollider/Data", "IsOverlaping", "IsOverlapping");
Mutate("VoxelEngine.BrickCollider/Data", "IsOverlaping", "GetBoundsAndSetTransform",
    instruction => instruction.OpCode = OpCodes.Nop,
    instruction => module.GetType("VoxelEngine.BrickCollider/Data").Methods.Single(method => method.Name == "IsOverlaping")
        .Body.Instructions.Last(candidate => candidate.Operand is MethodReference target && target.Name == "GetBoundsAndSetTransform"));
Mutate("VoxelEngine.VoxelPhysics", "IsOverlapping", "IsCollisionOff");
Mutate("VoxelEngine.BrickProperties", "IsOverlapValid", "isCollisionOff");
Mutate("Manager.SkyPhysics/QueryOverlapAabb", "OnElementIntersect", "IsOverlapValid");
Mutate("Manager.SkyPhysics/QueryOverlapAabb", "OnElementIntersect", "IsOverlapValid",
    instruction => instruction.OpCode = OpCodes.Ldfld, instruction => instruction.Previous.Previous);
Console.WriteLine("Selectable pass-through contract checks passed.");
