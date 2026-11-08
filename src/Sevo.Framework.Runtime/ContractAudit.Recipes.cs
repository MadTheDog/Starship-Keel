using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string SchematicType = "VoxelEngine.CraftingSchematic";
        private const string NativeSchematicTable = "Unity.Collections.NativeArray`1<" + SchematicType + ">";

        public static CapabilityResult EvaluateRecipes(ModuleDefinition module)
        {
            const string capability = "nativeRecipes";
            try
            {
                var allocate = Method(module, SchematicType, "AllocateResources", true, "System.Void");
                var register = Method(module, SchematicType, "Register", false, SchematicType);
                var renderLoad = Method(module, "Game.ItemInstanceRenderResources", "OnRepoLoaded", true, "System.Void", "System.Boolean");
                var release = Method(module, SchematicType, "ReleaseResources", true, "System.Void");
                if (allocate?.IsPublic != true || register?.IsPublic != true || !register.HasBody || renderLoad == null || release?.HasBody != true
                    || !Field(module, SchematicType, "Index", SchematicType + "[]", true)
                    || !Field(module, SchematicType, "schematicsNativeArray", NativeSchematicTable, true)
                    || !Field(module, "VoxelEngine.StorageContext", "schematics", NativeSchematicTable, false))
                    throw new InvalidOperationException("Native recipe tables or resource callbacks changed.");

                UniqueCall(renderLoad, SchematicType, "AllocateResources");
                var stock = UniqueCall(allocate, "VoxelEngine.TabSchematics", "WakeUp");
                var scrap = UniqueCall(allocate, SchematicType, "ComputeBrickScrapData");
                var codex = UniqueCall(register, SchematicType, "AddToCodexTab");
                var copy = allocate.Body.Instructions.Select((instruction, index) => (instruction, index))
                    .Single(entry => RecipeTableCall(entry.instruction, "set_Item", "System.Int32", "!0"));
                var capacity = allocate.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop)
                    .Take(7).ToArray();

                if (capacity.Length != 7
                    || !RecipeFieldInstruction(capacity[0], Code.Ldsfld, "Index")
                    || capacity[1].OpCode.Code != Code.Ldlen || capacity[2].OpCode.Code != Code.Conv_I4
                    || capacity[5].OpCode.Code != Code.Newobj || capacity[5].Operand is not MethodReference constructor
                    || constructor.DeclaringType.FullName != NativeSchematicTable || constructor.Name != ".ctor"
                    || !constructor.Parameters.Select(p => p.ParameterType.FullName)
                        .SequenceEqual(new[] { "System.Int32", "Unity.Collections.Allocator", "Unity.Collections.NativeArrayOptions" })
                    || !RecipeFieldInstruction(capacity[6], Code.Stsfld, "schematicsNativeArray")
                    || !(allocate.Body.Instructions.IndexOf(capacity[6]) < stock && stock < copy.index && copy.index < scrap)
                    || !RecipeFieldInstruction(copy.instruction.Previous?.Previous?.Previous, Code.Ldsflda, "schematicsNativeArray")
                    || !register.Body.Instructions.Take(codex).Any(instruction => RecipeFieldInstruction(instruction, Code.Ldsfld, "Index"))
                    || !release.Body.Instructions.Any(instruction => RecipeTableCall(instruction, "Dispose")))
                    throw new InvalidOperationException("Native recipe copy, registration or release ordering changed.");

                var context = Method(module, "VoxelEngine.StorageCommon", "get_CONTEXT", true, "VoxelEngine.StorageContext");
                if (context?.HasBody != true || !context.Body.Instructions.Any(instruction =>
                    RecipeFieldInstruction(instruction, Code.Ldsfld, "schematicsNativeArray")))
                    throw new InvalidOperationException("Storage jobs no longer bind the native schematic table.");

                var player = Method(module, "UI.Inventory.UI_PlayerCrafting", "RebuildList", false, "System.Void", "System.Boolean");
                var assembler = Method(module, "UI.UI_Assembler", "DeferredStart", false, "System.Void");
                if (player?.HasBody != true || assembler?.HasBody != true
                    || !player.Body.Instructions.Any(instruction => RecipeFieldInstruction(instruction, Code.Ldsfld, "SchematicsForCodex"))
                    || !assembler.Body.Instructions.Any(instruction => RecipeFieldInstruction(instruction, Code.Ldsfld, "SchematicsByCodexTab")))
                    throw new InvalidOperationException("The native player or assembler recipe list consumer changed.");

                return new CapabilityResult(capability, true,
                    "Native recipe table lifecycle and crafting consumers validated. Furnace candidate selection has a separate contract.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool RecipeFieldInstruction(Instruction? instruction, Code code, string field)
            => instruction?.OpCode.Code == code && instruction.Operand is FieldReference reference
                && reference.DeclaringType.FullName == SchematicType && reference.Name == field;

        private static bool RecipeTableCall(Instruction instruction, string name, params string[] parameters)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference method
                && method.DeclaringType.FullName == NativeSchematicTable && method.Name == name
                && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters);
    }
}
