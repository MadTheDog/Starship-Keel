using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks ordinary native paint independently of the fixed-color palette path.</summary>
        public static CapabilityResult EvaluateAttachmentPaint(ModuleDefinition module)
        {
            const string capability="attachmentPaint";
            const string brick="VoxelEngine.Brick", instance="VoxelEngine.BrickInstanceData", render="VoxelEngine.BrickRenderData";
            const string materialResources="VoxelEngine.VoxelMaterialResources", buffer="UnityEngine.ComputeBuffer";
            const string boolean="System.Boolean", color="UnityEngine.Color32", packed="System.UInt32", integer="System.Int32", noResult="System.Void";
            try
            {
                if(module.GetType("VoxelEngine.PaintMode")?.Fields.SingleOrDefault(field=>field.Name=="on" && field.IsLiteral)?.Constant is not 0)
                    throw new InvalidOperationException("The native paint-on mode changed.");
                var brickType=module.GetType(brick);
                foreach(string name in new[]{"canPaint","hasGlowPaint"})
                    if(brickType?.Fields.SingleOrDefault(field=>field.Name==name && !field.IsStatic
                        && field.IsPublic && !field.IsInitOnly && field.FieldType.FullName==boolean)==null)
                        throw new InvalidOperationException("The native brick paint flag is unavailable: "+name);

                var tool=Method(module,"VoxelEngine.ActionPaint","ColorBuildItem",false,noResult,boolean);
                var leftClick=Method(module,"VoxelEngine.ActionPaint","LeftClick",false,noResult);
                var action=Method(module,"NetworkedBuilder","LocalVoxelAction",false,noResult,"VoxelEngine.VoxelActionData",boolean,integer);
                var gate=tool?.Body?.Instructions.SingleOrDefault(instruction=>FieldUse(instruction,Code.Ldfld,brick,"canPaint",boolean));
                if(gate==null || gate.Next?.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                    || gate.Next.Next?.OpCode.Code!=Code.Ret || action==null
                    || leftClick?.Body?.Instructions.Any(instruction=>Calls(instruction,Code.Call,tool!))!=true
                    || !tool!.Body.Instructions.Any(instruction=>FieldUse(instruction,Code.Stfld,instance,"color",color))
                    || !tool.Body.Instructions.Any(instruction=>Calls(instruction,Code.Callvirt,action)))
                    throw new InvalidOperationException("The native paint tool no longer gates and submits instance color.");

                var constructor=Method(module,render,".ctor",false,noResult,packed,packed,instance,integer);
                var pack=Method(module,"Color32Extensions","Pack",true,packed,color);
                if(constructor?.HasBody!=true || pack==null || !constructor.Body.Instructions.Any(instruction=>
                    FieldUse(instruction,Code.Ldfld,instance,"color",color)
                    && Calls(instruction.Next,Code.Call,pack)
                    && FieldUse(instruction.Next?.Next,Code.Stfld,render,"color",packed)))
                    throw new InvalidOperationException("The native renderer no longer packs instance paint color.");

                var upload=Method(module,materialResources,"LoadBuffers",true,noResult);
                if(upload?.Body?.Instructions.Any(instruction=>instruction.OpCode.Code==Code.Ldstr
                    && Equals(instruction.Operand,"PaintData")
                    && FieldUse(instruction.Next,Code.Ldsfld,materialResources,"paintBuffer",buffer)
                    && instruction.Next?.Next is { } binding && binding.OpCode.Code==Code.Call
                    && binding.Operand is MethodReference method && method.Name=="SetGlobalBuffer"
                    && method.DeclaringType.FullName=="UnityEngine.Shader"
                    && method.Parameters.Select(parameter=>parameter.ParameterType.FullName)
                        .SequenceEqual(new[]{"System.String",buffer}))!=true)
                    throw new InvalidOperationException("The native paint palette shader binding changed.");

                return new CapabilityResult(capability,true,"Native paint tool, material mode and renderer color contracts validated.");
            }
            catch(Exception exception)
            {
                return new CapabilityResult(capability,false,exception.Message);
            }
        }

        private static bool FieldUse(Instruction? instruction,Code code,string owner,string name,string type) =>
            instruction?.OpCode.Code==code && instruction.Operand is FieldReference field
            && field.DeclaringType.FullName==owner && field.Name==name && field.FieldType.FullName==type;

        private static bool Calls(Instruction? instruction,Code code,MethodDefinition method) =>
            instruction?.OpCode.Code==code && instruction.Operand is MethodReference target && target.FullName==method.FullName;
    }
}
