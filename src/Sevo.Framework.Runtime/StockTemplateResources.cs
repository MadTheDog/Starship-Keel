using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal static class StockTemplateResources
    {
        private const string BrickType = "VoxelEngine.Brick";
        private const string MaterialType = "VoxelEngine.VoxelMaterial";
        private const string MaterialsType = "VoxelEngine.VoxelMaterial[]";
        private const string MaterialResourcesType = "VoxelEngine.VoxelMaterialResources";
        private const string RecipeType = "System.Object[]";
        private const string ResizeModeType = "VoxelEngine.ResizeMode";
        private const string NativeLoad = "LoadResourcesRuntime";
        private const string StockLoad = "LoadAllResourcesRuntime";
        private const string MaterialLookup = "CreateFaceIDArrayRuntime";
        private const string StockRecipe = "Hull/HullA";
        private static MethodInfo? loadResources;
        private static FieldInfo? argsField;
        private static FieldInfo? resizeModeField;

        [ThreadStatic]
        private static MaterialBinding? activeBinding;

        internal static CapabilityResult Evaluate(ModuleDefinition module)
        {
            const string capability = "stockTemplateMaterials";
            try
            {
                var ctor = RequireMethod(module, BrickType, ".ctor", false, "System.Void",
                    "System.Int32", "System.String", "System.String", ResizeModeType, RecipeType);
                var nativeLoad = RequireMethod(module, BrickType, NativeLoad, false, "System.Void");
                var stockLoad = RequireMethod(module, BrickType, StockLoad, true, "System.Void");
                var materialLookup = RequireMethod(module, MaterialType, MaterialLookup, true, MaterialsType, RecipeType);
                var setMaterials = RequireMethod(module, BrickType, "set_VoxelMaterials", false, "System.Void", MaterialsType);
                var args = RequireRecipeField(ctor.DeclaringType, "args", RecipeType);
                var resizeMode = RequireRecipeField(ctor.DeclaringType, "resizeMode", ResizeModeType);

                if (!nativeLoad.IsFamily || !nativeLoad.IsVirtual || !materialLookup.IsPublic
                    || !StoresArgument(ctor, args, 4) || !StoresArgument(ctor, resizeMode, 3))
                    throw new InvalidOperationException("Native brick recipe or loader signature changed.");

                var loadBody = Body(nativeLoad);
                int lookupIndex = FindCall(loadBody, materialLookup);
                if (lookupIndex < 2 || lookupIndex + 1 >= loadBody.Length
                    || loadBody[lookupIndex - 2].OpCode.Code != Code.Ldarg_0
                    || loadBody[lookupIndex - 1].OpCode.Code != Code.Ldfld
                    || !UsesField(loadBody[lookupIndex - 1], args)
                    || FindCall(loadBody, setMaterials) != lookupIndex + 1)
                    throw new InvalidOperationException("Native brick loader no longer assigns the material lookup for its own recipe.");

                var stockBody = Body(stockLoad);
                int nativeLoadIndex = FindCall(stockBody, nativeLoad);
                if (stockBody[nativeLoadIndex].OpCode.Code != Code.Callvirt
                    || !stockBody.Take(nativeLoadIndex).Any(instruction => instruction.OpCode.Code == Code.Ldsfld
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == BrickType
                        && field.Name == "BRICK_INDEX" && field.FieldType.FullName == BrickType + "[]")
                    || !stockBody.Take(nativeLoadIndex).Any(instruction => instruction.OpCode.Code == Code.Ldelem_Ref))
                    throw new InvalidOperationException("Native stock brick loading loop changed.");

                var awakeBody = Body(RequireMethod(module, "ResourcesLoader", "Awake", false, "System.Void"));
                int collider = FindCall(awakeBody, RequireMethod(module, "VoxelEngine.BrickCollider", "AllocateResources", true, "System.Void"));
                int stock = FindCall(awakeBody, stockLoad);
                int allocation = FindCall(awakeBody, RequireMethod(module, "Manager.GameResourcesManager", "AllocateResources", true, "System.Void"));
                if (!(collider < stock && stock < allocation))
                    throw new InvalidOperationException("Stock template loading is outside the native resource initialization window.");

                var materialBody = Body(materialLookup);
                var getBaked = RequireMethod(module, MaterialResourcesType, "get_materialParamDatas", true, MaterialsType);
                var getCount = RequireMethod(module, MaterialResourcesType, "get_MaterialCount", true, "System.Int32");
                var setCount = RequireMethod(module, MaterialResourcesType, "set_MaterialCount", true, "System.Void", "System.Int32");
                int bakedIndex = FindCall(materialBody, getBaked);
                int countWrite = FindCall(materialBody, setCount);
                if (!getBaked.IsPublic || !getCount.IsPublic || !setCount.IsPublic
                    || bakedIndex + 2 >= materialBody.Length || countWrite < bakedIndex + 6
                    || !IsCall(materialBody[bakedIndex + 1], getCount)
                    || materialBody[bakedIndex + 2].OpCode.Code != Code.Ldelem_Any
                    || (materialBody[bakedIndex + 2].Operand as TypeReference)?.FullName != MaterialType
                    || !IsCall(materialBody[countWrite - 3], getCount)
                    || materialBody[countWrite - 2].OpCode.Code != Code.Ldc_I4_1
                    || materialBody[countWrite - 1].OpCode.Code != Code.Add)
                    throw new InvalidOperationException("Stock baked material lookup or cursor update changed.");

                return new CapabilityResult(capability, true, "Native stock template loading and scoped baked material reuse are available.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        internal static void Install(Harmony harmony, Assembly game)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var brick = game.GetType(BrickType, throwOnError: true)!;
            var nativeLoad = brick.GetMethod(NativeLoad, instance, null, Type.EmptyTypes, null);
            var recipe = brick.GetField("args", instance);
            var resizeMode = brick.GetField("resizeMode", instance);
            var material = game.GetType(MaterialType, throwOnError: true)!;
            var lookup = material.GetMethod(MaterialLookup, BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(object[]) }, null);
            if (nativeLoad == null || !nativeLoad.IsFamily || !nativeLoad.IsVirtual || nativeLoad.ReturnType != typeof(void)
                || recipe?.FieldType != typeof(object[]) || resizeMode?.FieldType != typeof(ResizeMode)
                || lookup?.ReturnType != typeof(VoxelMaterial[]))
                throw new InvalidOperationException("The live stock material binding does not match its audit.");

            var prefix = typeof(StockTemplateResources).GetMethod(nameof(UseStockMaterials), BindingFlags.Static | BindingFlags.NonPublic)!;
            harmony.Patch(lookup, prefix: new HarmonyMethod(prefix));
            argsField = recipe;
            resizeModeField = resizeMode;
            loadResources = nativeLoad;
        }

        internal static bool IsSupported(Brick brick,Brick template,bool legacyHull=false) => TryBind(brick,template,legacyHull)!=null;

        internal static void Load(Brick brick,Brick template,bool legacyHull=false)
        {
            var load = loadResources ?? throw new InvalidOperationException("Stock material patch is not installed.");
            var binding = TryBind(brick,template,legacyHull) ?? throw new InvalidOperationException("Managed brick does not match the loaded stock recipe.");
            if (activeBinding != null)
                throw new InvalidOperationException("Managed stock resource loads cannot be nested.");

            int materialCount = VoxelMaterialResources.MaterialCount;
            bool cursorChanged = false;
            activeBinding = binding;
            try
            {
                load.Invoke(brick, null);
            }
            finally
            {
                activeBinding = null;
                cursorChanged = VoxelMaterialResources.MaterialCount != materialCount;
                if (cursorChanged)
                    VoxelMaterialResources.MaterialCount = materialCount;
            }
            if (cursorChanged || !binding.Used || !ReferenceEquals(brick.VoxelMaterials, binding.Materials))
                throw new InvalidOperationException("Managed resource loading did not reuse stock materials without advancing the native cursor.");
        }

        internal static bool IsTemplateAvailable(Brick? template, out string reason)
        {
            if (template == null || template.SubShapes != null || template.IsDecal || template.IsCollisionOff
                || template.isRepeatBrick || template.hasRepeatBrick || template.childrenBrickID != 0
                || string.IsNullOrWhiteSpace(template.nameMesh) || template.VoxelMesh == null || template.VoxelMesh.Mesh == null
                || template.VoxelCollider?.subShapes == null || template.VoxelCollider.subShapes.Length != 1
                || template.VoxelCollider.subShapes[0].shape != template.colliderShape)
            {
                reason = "The stock single-shape mesh or collider is unavailable.";
                return false;
            }
            if (Resources.Load<Mesh>("Shapes/" + template.nameMesh + "_preview") != null)
            {
                // The native mesh constructor edits preview UVs in place. Only the private-clone path is safe here.
                reason = "The stock mesh now uses a shared preview asset.";
                return false;
            }

            var materials = template.VoxelMaterials;
            var baked = VoxelMaterialResources.materialParamDatas;
            if (materials == null || materials.Length == 0 || baked == null
                || materials.Any(material => material.Id < 0 || material.Id >= baked.Length || baked[material.Id].Id != material.Id))
            {
                reason = "The baked stock material bindings are unavailable.";
                return false;
            }
            reason = "";
            return true;
        }

        internal static (ResizeMode ResizeMode, object[] Args) CopyRecipe(Brick template)
        {
            if (argsField?.GetValue(template) is not object[] recipe || recipe.Length == 0
                || resizeModeField?.GetValue(template) is not ResizeMode resizeMode
                || !resizeMode.Equals(template.VoxelMesh.ResizeMode))
                throw new InvalidOperationException("The stock template does not expose a compatible mesh recipe.");
            return (resizeMode, (object[])recipe.Clone());
        }

        private static MaterialBinding? TryBind(Brick brick,Brick template,bool legacyHull)
        {
            if (!IsTemplateAvailable(template,out _))
                return null;

            if (argsField?.GetValue(brick) is not object[] recipe
                || resizeModeField?.GetValue(brick) is not ResizeMode resizeMode || !resizeMode.Equals(template.VoxelMesh.ResizeMode)
                || !string.Equals(brick.nameMesh, template.nameMesh, StringComparison.Ordinal)
                || brick.SubShapes != null || brick.IsDecal || brick.IsCollisionOff || brick.colliderShape != template.colliderShape
                || brick.isRepeatBrick || brick.hasRepeatBrick || brick.childrenBrickID != 0
                || !brick.boundsOverride.Equals(template.boundsOverride) || !brick.boundsCulling.Equals(template.boundsCulling)
                || brick.isDynamicGridSize != template.isDynamicGridSize || brick.gridLevel != template.gridLevel
                || brick.minGridSize != template.minGridSize || brick.scaleUV != template.scaleUV
                || brick.isPipeSeeThrough != template.isPipeSeeThrough || brick.isFuelSeeThrough != template.isFuelSeeThrough
                || brick.CollisionWidth != template.CollisionWidth || brick.CollisionScaleFactor != template.CollisionScaleFactor
                || brick.isConveyorBelt != template.isConveyorBelt)
                return null;

            if (legacyHull)
            {
                if (!resizeMode.Equals(ResizeMode.BlockFull) || recipe.Length != 1
                    || !string.Equals(recipe[0] as string, StockRecipe, StringComparison.Ordinal))
                    return null;
            }
            else if (argsField.GetValue(template) is not object[] stockRecipe
                || ReferenceEquals(recipe, stockRecipe) || !recipe.SequenceEqual(stockRecipe))
                return null;

            return new MaterialBinding(recipe, template.VoxelMaterials);
        }

        private static bool UseStockMaterials(object[] __0, ref VoxelMaterial[] __result)
        {
            var binding = activeBinding;
            if (binding == null || !ReferenceEquals(__0, binding.Recipe))
                return true;
            if (binding.Used)
                throw new InvalidOperationException("The managed stock recipe was unexpectedly used more than once.");
            binding.Used = true;
            __result = binding.Materials;
            return false;
        }

        private sealed class MaterialBinding
        {
            internal readonly object[] Recipe;
            internal readonly VoxelMaterial[] Materials;
            internal bool Used;

            internal MaterialBinding(object[] recipe, VoxelMaterial[] materials)
            {
                Recipe = recipe;
                Materials = materials;
            }
        }

        private static MethodDefinition RequireMethod(ModuleDefinition module, string type, string name, bool isStatic,
            string result, params string[] parameters) =>
            module.GetType(type)?.Methods.SingleOrDefault(method => method.Name == name && method.IsStatic == isStatic
                && method.ReturnType.FullName == result
                && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                ?? throw new InvalidOperationException("Missing native signature: " + type + "." + name);

        private static FieldDefinition RequireRecipeField(TypeDefinition brick, string name, string type) =>
            brick.Fields.SingleOrDefault(field => field.Name == name && field.FieldType.FullName == type
                && field.IsPrivate && field.IsInitOnly && !field.IsStatic)
                ?? throw new InvalidOperationException("Missing native recipe field: " + name);

        private static bool StoresArgument(MethodDefinition method, FieldDefinition field, int parameterIndex)
        {
            var instructions = Body(method);
            for (int index = 2; index < instructions.Length; index++)
                if (instructions[index].OpCode.Code == Code.Stfld && UsesField(instructions[index], field)
                    && instructions[index - 2].OpCode.Code == Code.Ldarg_0
                    && instructions[index - 1].Operand is ParameterDefinition parameter && parameter.Index == parameterIndex)
                    return true;
            return false;
        }

        private static Instruction[] Body(MethodDefinition method) =>
            method.HasBody ? method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray()
                : throw new InvalidOperationException("Missing native method body: " + method.FullName);

        private static bool UsesField(Instruction instruction, FieldDefinition field) =>
            instruction.Operand is FieldReference actual && actual.FullName == field.FullName;

        private static bool IsCall(Instruction instruction, MethodDefinition method) =>
            (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
            && instruction.Operand is MethodReference actual && actual.FullName == method.FullName;

        private static int FindCall(Instruction[] body, MethodDefinition method)
        {
            int index = Array.FindIndex(body, instruction => IsCall(instruction, method));
            if (index < 0 || Array.FindLastIndex(body, instruction => IsCall(instruction, method)) != index)
                throw new InvalidOperationException("Missing or ambiguous native call: " + method.FullName);
            return index;
        }
    }
}
