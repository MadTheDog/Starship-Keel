using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the native terrain material table and packed material identities.</summary>
        public static CapabilityResult EvaluateTerrainMaterialRegistration(ModuleDefinition module)
        {
            const string capability = "terrainMaterialRegistration";
            const string voxel = "World.Terrain.VoxelNodeData";
            try
            {
                var (_, load, _) = RequireTerrainPublicationBridge(module);
                if (!Field(module, voxel, "texId0", "System.Byte", false)
                    || !Field(module, voxel, "texId1", "System.Byte", false))
                    throw new InvalidOperationException("Native voxel material identity is no longer byte sized.");
                var instructions = load.Body.Instructions;
                var publish = instructions.Single(instruction => IsCall(instruction, MaterialResources, "PushMaterialDatas"));
                var count = instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Ldstr
                    && Equals(instruction.Operand, "_TerrainMaterialCount"));
                if (count == null || count.Offset <= publish.Offset
                    || !IsTerrainTextureField(count.Next, MaterialResources, "TerrainMaterialDatas", Code.Ldsflda)
                    || !IsTerrainTextureCall(count.Next.Next, MaterialArray, "get_Length", "System.Int32")
                    || !IsTerrainTextureCall(count.Next.Next.Next, "UnityEngine.Shader", "SetGlobalInt", "System.Void",
                        "System.String", "System.Int32"))
                    throw new InvalidOperationException("The shader material count no longer follows the published terrain table.");
                var getter = Method(module, voxel, "get_MainTexId", false, "System.Int32");
                if (getter?.HasBody != true || !UsesTerrainTextureField(getter, voxel, "texId0", Code.Ldfld)
                    || !UsesTerrainTextureField(getter, voxel, "texId1", Code.Ldfld))
                    throw new InvalidOperationException("Native terrain material lookup changed.");
                return new CapabilityResult(capability, true, "Native terrain material publication and packed byte IDs validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
