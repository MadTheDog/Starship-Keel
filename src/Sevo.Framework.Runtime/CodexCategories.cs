using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;
using UI.Codex;

namespace Sevo.Framework.Runtime
{
    internal static class CodexCategories
    {
        internal static CodexTab? Find(string name,IReadOnlyList<string> menus)
        {
            const BindingFlags fields=BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly;
            if(typeof(CodexTab).GetField(name,fields)?.GetValue(null) is not CodexTab category
                || category.items==null || Item.CodexItems==null)
                return null;
            int appearances=0;
            foreach(var menu in menus)
                if(typeof(UI.UI_Codex).GetField(menu,fields)?.GetValue(null) is List<CodexTab> list)
                    appearances+=list.Count(entry=>ReferenceEquals(entry,category));
            return appearances==1 ? category : null;
        }
    }

    public static partial class ContractAudit
    {
        internal static CapabilityResult EvaluateCodexCategories(ModuleDefinition module,out string[] menus)
        {
            const string capability="codexCategories", tab="UI.Codex.CodexTab", ui="UI.UI_Codex", brick="VoxelEngine.Brick";
            const string items="System.Collections.Generic.List`1<Item>";
            const string tabs="System.Collections.Generic.List`1<"+tab+">";
            menus=Array.Empty<string>();
            try
            {
                var registration=Method(module,brick,"Tab",false,brick,tab,"System.Boolean");
                if(registration?.IsPublic!=true || !registration.HasBody
                    || !HasList(module.GetType("Item"),"CodexItems",items,isStatic:true)
                    || !HasList(module.GetType(tab),"items",items,isStatic:false))
                    throw new InvalidOperationException("The native Codex registration signature or item lists changed.");

                var body=registration.Body.Instructions.Where(instruction=>instruction.OpCode.Code!=Code.Nop).ToArray();
                var appends=body.Select((instruction,index)=>(instruction,index))
                    .Where(entry=>CallsListAdd(entry.instruction,items)).Select(entry=>entry.index).ToArray();
                if(body.Length<5 || body[0].OpCode.Code!=Code.Ldarg_2
                    || body[1].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || !ReferenceEquals(body[1].Operand,body[4])
                    || body[2].OpCode.Code!=Code.Ldarg_0 || body[3].OpCode.Code!=Code.Ret
                    || appends.Length!=2
                    || !IsItemAppend(body,appends[0],Code.Ldsfld,"Item","CodexItems")
                    || !IsItemAppend(body,appends[1],Code.Ldfld,tab,"items"))
                    throw new InvalidOperationException("Native Codex registration no longer appends once to the global and category lists.");

                var awake=Method(module,ui,"Awake",false,"System.Void");
                if(awake?.HasBody!=true) throw new InvalidOperationException("The native Codex menu setup is unavailable.");
                string? menu=null;
                var visible=new List<string>();
                foreach(var instruction in awake.Body.Instructions)
                {
                    if(instruction.OpCode.Code==Code.Ldsfld && instruction.Operand is FieldReference field
                        && field.FieldType.FullName==tabs)
                    {
                        menu=field.DeclaringType.FullName==ui && HasList(awake.DeclaringType,field.Name,tabs,isStatic:true)
                            && instruction.Next?.OpCode.Code==Code.Ldarg_0
                            && instruction.Next.Next?.Operand is FieldReference parent
                            && parent.FieldType.FullName=="UnityEngine.Transform" ? field.Name : null;
                    }
                    else if(instruction.OpCode.Code==Code.Call && instruction.Operand is MethodReference call
                        && call.DeclaringType.FullName=="UI.ItemStorage" && call.Name=="CreateSlots"
                        && call.Parameters.FirstOrDefault()?.ParameterType.FullName==tabs)
                    {
                        if(menu!=null) visible.Add(menu);
                        menu=null;
                    }
                }
                menus=visible.Distinct(StringComparer.Ordinal).ToArray();
                if(menus.Length==0) throw new InvalidOperationException("No native item category lists feed the visible Codex.");
                return new CapabilityResult(capability,true,"Native category lists and single Codex registration validated.");
            }
            catch(Exception exception)
            {
                return new CapabilityResult(capability,false,exception.Message);
            }
        }

        private static bool HasList(TypeDefinition? owner,string name,string type,bool isStatic) =>
            owner?.Fields.Any(field=>field.Name==name && field.IsPublic && field.IsStatic==isStatic
                && field.FieldType.FullName==type)==true;

        private static bool CallsListAdd(Instruction instruction,string list) =>
            instruction.OpCode.Code==Code.Callvirt && instruction.Operand is MethodReference method
            && method.DeclaringType.FullName==list && method.Name=="Add" && method.Parameters.Count==1;

        private static bool IsItemAppend(Instruction[] body,int index,Code load,string owner,string fieldName) =>
            index>=2 && body[index-1].OpCode.Code==Code.Ldarg_0 && body[index-2].OpCode.Code==load
            && body[index-2].Operand is FieldReference field && field.DeclaringType.FullName==owner && field.Name==fieldName;
    }
}
