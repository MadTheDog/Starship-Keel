using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the terrain texture bindings and the row publication before the far planet bake.</summary>
        public static CapabilityResult EvaluateTerrainTextureOverrides(ModuleDefinition module)
        {
            const string capability = "terrainTextureOverrides";
            const string loader = "ResourcesLoader";
            const string array = "UnityEngine.Texture2DArray";
            const string materialSource = MaterialResources + "/TerrainTexture";
            try
            {
                if (!HasTerrainTextureAlias(module, loader, "worldTerrainDiffuse", array, true)
                    || !HasTerrainTextureAlias(module, loader, "worldTerrainNormal", array, true)
                    || !HasTerrainTextureAlias(module, MaterialResources, "ta_worldTerrainDiffuse", array, false)
                    || !Field(module, MaterialResources, "terrainTextures",
                        "System.Collections.Generic.List`1<" + materialSource + ">", false)
                    || !Field(module, materialSource, "ShaderName", "System.String", false)
                    || !Field(module, MaterialResources, "TerrainMaterialDatas", MaterialArray, true)
                    || !Field(module, MaterialRow, "textureId", "System.Int32", false)
                    || !Field(module, MaterialRow, "fadeValue", "Unity.Mathematics.float4", false))
                    throw new InvalidOperationException("Terrain texture aliases or material row fields changed.");

                var awake = Method(module, loader, "Awake", false, "System.Void");
                var quality = Method(module, loader, "ApplyTerrainTextureQuality", true, "System.Void");
                if (awake?.HasBody != true || quality?.HasBody != true
                    || !HasTerrainTextureBinding(awake, "WORLD_DIFFUSE_TEX", "ta_worldTerrainDiffuse")
                    || !HasTerrainTextureBinding(awake, "WORLD_NORMAL_TEX", "ta_worldTerrainNormal")
                    || !UsesTerrainTextureField(quality, loader, "worldTerrainDiffuse", Code.Ldsfld)
                    || !UsesTerrainTextureField(quality, loader, "worldTerrainNormal", Code.Ldsfld))
                    throw new InvalidOperationException("Terrain texture shader binding or quality lifecycle changed.");

                var (publish, load, upload) = RequireTerrainPublicationBridge(module);
                var stockPush = load.Body.Instructions.Single(instruction => IsCall(instruction, MaterialResources, "PushMaterialDatas"));
                var gradients = load.Body.Instructions.SingleOrDefault(instruction => IsCall(instruction, MaterialResources, "RegenerateMaterialGradients"));
                var bake = Method(module, MaterialResources, "RegenerateMaterialGradients", true, "System.Void");
                if (gradients == null || stockPush.Offset >= gradients.Offset
                    || bake?.HasBody != true || !bake.Body.Instructions.Any(instruction =>
                        IsCall(instruction, "World.Terrain.Planet.PlanetTerrainTextureResources", "RebuildPlanetTextures"))
                    || !publish.Body.Instructions.Any(instruction => instruction.Offset < upload.Offset
                        && IsTerrainTextureCall(instruction, array, "GetPixels", "UnityEngine.Color[]", "System.Int32", "System.Int32"))
                    || !UsesTerrainTextureField(publish, MaterialResources, "ta_worldTerrainDiffuse", Code.Ldfld)
                    || !UsesTerrainTextureField(publish, MaterialRow, "textureId", Code.Stfld)
                    || !UsesTerrainTextureField(publish, MaterialRow, "fadeValue", Code.Stfld))
                    throw new InvalidOperationException("Terrain texture indices or diffuse mip colors no longer publish before the planet bake.");

                return new CapabilityResult(capability, true, "Native terrain texture publication and cleanup validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static (MethodDefinition Publish, MethodDefinition Load, Instruction Upload) RequireTerrainPublicationBridge(ModuleDefinition module)
        {
            if (!Field(module, MaterialResources, "TerrainMaterialDatas", MaterialArray, true))
                throw new InvalidOperationException("Native terrain material table changed.");
            var publish = Method(module, MaterialResources, "PushMaterialDatas", false, "System.Void");
            var load = Method(module, MaterialResources, "LoadResources", false, "System.Void", "System.Boolean");
            if (publish?.HasBody != true || load?.HasBody != true
                || load.Body.Instructions.Count(instruction => IsCall(instruction, MaterialResources, "PushMaterialDatas")) != 1)
                throw new InvalidOperationException("Terrain material publication changed.");
            var upload = publish.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Callvirt
                && instruction.Operand is MethodReference call && call.Name == "PushData"
                && call.DeclaringType.FullName == "Common.ComputeBufferHandler`1<" + MaterialRow + ">"
                && call.ReturnType.FullName == "System.Void" && call.Parameters.Count == 2
                && call.Parameters[1].ParameterType.FullName == "System.Int32");
            if (upload == null || !publish.Body.Instructions.Any(instruction => instruction.Offset < upload.Offset
                && IsTerrainTextureField(instruction, MaterialResources, "TerrainMaterialDatas", Code.Ldsfld)))
                throw new InvalidOperationException("Terrain material upload changed.");
            var title = Method(module, MaterialResources, "UnloadResourcesOnQuitToTitle", false, "System.Void");
            var release = Method(module, MaterialResources, "ReleaseResources", true, "System.Void");
            if (title?.HasBody != true || release?.HasBody != true
                || !UsesTerrainTextureField(title, MaterialResources, "TerrainMaterialDatas", Code.Ldsflda)
                || !UsesTerrainTextureField(release, MaterialResources, "TerrainMaterialDatas", Code.Ldsflda))
                throw new InvalidOperationException("Terrain resource release lifecycle changed.");
            return (publish, load, upload);
        }

        private static bool HasTerrainTextureBinding(MethodDefinition method, string globalName, string field)
        {
            return method.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldstr
                && Equals(instruction.Operand, globalName)
                && instruction.Next?.OpCode.Code == Code.Ldarg_0
                && IsTerrainTextureField(instruction.Next.Next, "ResourcesLoader", field, Code.Ldfld)
                && IsTerrainTextureCall(instruction.Next.Next.Next, "UnityEngine.Shader", "SetGlobalTexture", "System.Void",
                    "System.String", "UnityEngine.Texture"));
        }

        private static bool HasTerrainTextureAlias(ModuleDefinition module, string type, string name, string fieldType, bool isStatic) =>
            module.GetType(type)?.Fields.Any(field => field.Name == name && field.FieldType.FullName == fieldType
                && field.IsStatic == isStatic) == true;

        private static bool IsTerrainTextureCall(Instruction? instruction, string type, string name, string result, params string[] parameters) =>
            (instruction?.OpCode.Code == Code.Call || instruction?.OpCode.Code == Code.Callvirt)
            && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name
            && method.ReturnType.FullName == result && method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(parameters);

        private static bool UsesTerrainTextureField(MethodDefinition method, string type, string field, Code opcode) =>
            method.Body.Instructions.Any(instruction => IsTerrainTextureField(instruction, type, field, opcode));

        private static bool IsTerrainTextureField(Instruction? instruction, string type, string field, Code opcode) =>
            instruction?.OpCode.Code == opcode && instruction.Operand is FieldReference reference
            && reference.DeclaringType.FullName == type && reference.Name == field;
    }
}
