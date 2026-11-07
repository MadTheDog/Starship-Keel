using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateModularReceiverRendering(ModuleDefinition module)
        {
            const string capability = "modularReceiverRendering";
            const string brick = "VoxelEngine.Brick", data = "VoxelEngine.BrickInstanceData";
            const string render = "VoxelEngine.RenderData", renderer = "VoxelEngine.EvoRenderer";
            const string ids = "VoxelEngine.BrickIds", culling = "VoxelEngine.BrickCullingBuffer";
            const string resources = "VoxelEngine.BrickRenderDataResources";
            const string v = "System.Void", i = "System.Int32", b = "System.Boolean";
            try
            {
                MethodDefinition Need(string owner, string name, bool isStatic, string result, params string[] parameters) =>
                    Method(module, owner, name, isStatic, result, parameters) is { HasBody: true } found ? found
                    : throw new InvalidOperationException("Missing modular rendering contract: " + owner + "." + name);
                Instruction[] Body(MethodDefinition method) => method.Body.Instructions
                    .Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                bool Call(Instruction instruction, string owner, string name) =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && instruction.Operand is MethodReference called
                    && called.DeclaringType.FullName == owner && called.Name == name;
                int Unique(Instruction[] body, string owner, string name)
                {
                    var matches = body.Select((instruction, index) => (instruction,index))
                        .Where(pair => Call(pair.instruction, owner, name)).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("Ambiguous modular rendering call: " + owner + "." + name);
                    return matches[0].index;
                }
                bool Field(Instruction instruction, Code code, string owner, string name) =>
                    instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == owner && field.Name == name;
                void Require(bool condition, string reason)
                { if (!condition) throw new InvalidOperationException(reason); }

                var bind = Need(brick, "SetBrickData", true, v, render + "&", data, i, i, b);
                Require(bind.IsPrivate, "Render binding visibility changed.");
                var submit = Body(Need(brick, "SetRenderData", true, i, render, data, renderer, i, i, b));
                Require(submit.Length > 7 && submit[0].OpCode.Code == Code.Ldarg_0
                    && Field(submit[1], Code.Ldfld, render, "clusterCount")
                    && submit[2].OpCode.Code is Code.Brtrue or Code.Brtrue_S
                    && ReferenceEquals(submit[2].Operand, submit[5])
                    && submit[3].OpCode.Code == Code.Ldc_I4_0 && submit[4].OpCode.Code == Code.Ret
                    && submit[5].OpCode.Code is Code.Ldarga or Code.Ldarga_S,
                    "Render binding no longer follows the native nonzero geometry guard.");
                Require(Unique(submit, brick, "SetBrickData") > 5
                    && Unique(submit, brick, "SetBrickData") < Unique(submit, renderer, "AddElement"),
                    "Native render binding no longer precedes carrier allocation.");

                var add = Body(Need(renderer, "AddElement", false, i, render));
                Require(!add.Any(instruction => instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
                    && add.Any(instruction => Field(instruction, Code.Ldfld, render, "clusterCount"))
                    && Unique(add, renderer, "SetNewCounts") < Unique(add, culling, "AddElement"),
                    "Native renderer no longer accepts zero-cluster carrier records.");
                var counts = Body(Need(renderer,"SetNewCounts",false,v,i,i,i));
                Require(counts.Select((instruction,index) => (instruction,index)).Any(pair => pair.index >= 2
                    && pair.instruction.OpCode.Code == Code.Mul
                    && counts[pair.index-2].OpCode.Code == Code.Ldarg_3
                    && counts[pair.index-1].OpCode.Code == Code.Ldarg_2
                    && pair.index+1 < counts.Length && counts[pair.index+1].OpCode.Code == Code.Add),
                    "Zero-cluster records no longer contribute zero to native draw counts.");
                var allocate = Body(Need(culling, "AddElement", false, i, render));
                Require(!allocate.Any(instruction => instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
                    && allocate.Any(instruction => instruction.Operand is MethodReference method && method.Name == "GetIdAndSetFast")
                    && allocate.Any(instruction => Call(instruction,culling,"SetDirty")),
                    "Culling carrier allocation changed.");

                var register = Need(brick,"RegisterRenderData",false,v,data,renderer,i,ids+"&",b);
                var update = Need(brick,"UpdateRenderData",false,v,data,renderer,i,ids,"VoxelEngine.VoxelEntity",b,b);
                Require(register.IsPublic && !register.HasGenericParameters && update.IsPublic,
                    "Native render redirect signatures changed.");
                var generic = module.GetType(brick).Methods.SingleOrDefault(method => method.Name == "RegisterRenderData"
                    && method.HasGenericParameters && method.Parameters.Count == 7 && method.HasBody)
                    ?? throw new InvalidOperationException("Native generic render allocation is unavailable.");
                var genericBody = Body(generic);
                Require(Unique(genericBody,resources,"RegisterIndex") < Unique(genericBody,resources,"SetData")
                    && genericBody.Any(instruction => Call(instruction,"VoxelEngine.IBrickRenderIds","set_renderId1")),
                    "Native render data ownership changed.");
                var updateData = Body(Need(brick,"UpdateBrickRenderData",false,i,data,renderer,i,ids));
                Require(Unique(updateData,renderer,"GetElement") < Unique(updateData,resources,"SetDataImmediate")
                    && updateData.Any(instruction => Call(instruction,ids,"get_renderId1")),
                    "Native primary handle no longer owns render data updates.");
                var updateRecord = Body(Need(brick,"UpdateRenderData",true,v,i,render,data,renderer,i,i,b));
                Require(Unique(updateRecord,brick,"SetBrickData") < Unique(updateRecord,renderer,"UpdateRenderData"),
                    "Native updates no longer apply render binding before publication.");

                var release = module.GetType(brick).Methods.SingleOrDefault(method => method.Name == "ReleaseRenderData"
                    && method.HasGenericParameters && method.HasBody)
                    ?? throw new InvalidOperationException("Native render data release is unavailable.");
                var releaseBody = Body(release);
                int firstFree = Array.FindIndex(releaseBody, instruction => Call(instruction,resources,"FreeIndex"));
                int firstBranch = Array.FindIndex(releaseBody, instruction => instruction.OpCode.FlowControl == FlowControl.Cond_Branch);
                Require(firstFree > 0 && firstFree < firstBranch
                    && releaseBody.Take(firstFree).Count(instruction => Call(instruction,renderer,"GetDataId")) == 1
                    && releaseBody.Take(firstFree).Count(instruction => Call(instruction,renderer,"RemoveElement")) == 1
                    && releaseBody.Take(firstFree).Any(instruction => Call(instruction,"VoxelEngine.IBrickRenderIds","get_renderId1"))
                    && releaseBody.Count(instruction => Call(instruction,resources,"FreeIndex")) == 3,
                    "Native primary/secondary render data cleanup changed.");
                foreach (var free in releaseBody.Select((instruction,index) => (instruction,index))
                    .Where(pair => Call(pair.instruction,resources,"FreeIndex")).Skip(1))
                {
                    int branch = free.index-2;
                    Require(branch >= 2 && releaseBody[branch].OpCode.Code is Code.Beq or Code.Beq_S
                        && free.index+1 < releaseBody.Length
                        && ReferenceEquals(releaseBody[branch].Operand,releaseBody[free.index+1])
                        && releaseBody[branch-2].OpCode.Code == Code.Ldloc_0
                        && releaseBody[branch-1].OpCode.Code == releaseBody[free.index-1].OpCode.Code,
                        "Native preview no longer deduplicates shared render data release.");
                }

                var aabb = Body(Need(data,"GetAabb",false,"Common.Aabb",b));
                foreach (var method in new[] { "get_GridSize", "get_Position", "get_Quaternion", "get_ScaleFloat" })
                    Require(aabb.Any(instruction => Call(instruction,data,method)), "Native culling coordinate conversion changed: " + method);
                Require(aabb.Any(instruction => Call(instruction,brick,"get_Unit"))
                    && aabb.Any(instruction => Call(instruction,"UnityEngine.Quaternion","op_Multiply"))
                    && aabb.Any(instruction => Call(instruction,"Vector3Extensions","Abs"))
                    && aabb.Any(instruction => Call(instruction,"Common.Aabb",".ctor")),
                    "Native culling bounds construction changed.");
                var binding = Body(bind);
                int bounds = Unique(binding,data,"GetAabb");
                Require(bounds > 0 && binding[bounds-1].OpCode.Code == Code.Ldc_I4_1
                    && binding.Any(instruction => Field(instruction,Code.Stfld,render,"bounds"))
                    && binding.Any(instruction => Field(instruction,Code.Stfld,render,"dataId")),
                    "Native binding no longer uses render bounds and the assigned data owner.");
                foreach (var field in new[] { "clusterStart", "clusterCount", "clusterStartLOD", "clusterCountLOD" })
                    Require(module.GetType(render).Fields.Any(member => member.Name == field && member.FieldType.FullName == i
                        && member.IsPublic && !member.IsStatic), "Native geometry span changed: " + field);
                return new CapabilityResult(capability,true,
                    "Native render carrier allocation, updates, cleanup and culling coordinate contracts are available; previews retain native first-bay behavior.");
            }
            catch (Exception exception) { return new CapabilityResult(capability,false,exception.Message); }
        }
    }
}
