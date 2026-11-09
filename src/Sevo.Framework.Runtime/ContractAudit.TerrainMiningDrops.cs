using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the managed stock terrain mining drop and registered item component lifecycle.</summary>
        public static CapabilityResult EvaluateTerrainMiningDrops(ModuleDefinition module)
        {
            const string capability = "terrainMiningDrops";
            const string octree = "World.Terrain.OctreeNodeResources";
            const string voxel = "World.Terrain.VoxelNodeData";
            const string item = "Game.ItemInstanceData";
            const string loop = "Manager.GameloopManager";
            const string voidType = "System.Void";
            const string integer = "System.Int32";
            const string boolean = "System.Boolean";
            try
            {
                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters) =>
                    Method(module, type, name, isStatic, result, parameters)
                    ?? throw new InvalidOperationException("Terrain mining dependency changed: " + type + "." + name);
                var drop = Require(octree, "CheckIfDropRock", true, voidType, integer, "Manager.ImpactData", voxel,
                    boolean, "World.Terrain.OctreeNodeData", "Unity.Mathematics.int3");
                var edit = Require(octree, "UpdateDensitiesOnNode", true, voidType, integer,
                    "Unity.Mathematics.float3", "Manager.ImpactData", "Vector3l");
                var spawn = Require(item, "SpawnRandomItem", true, integer, item + "&", "Vector3l",
                    "Unity.Mathematics.float2", "World.WorldPropCommon/WorldPropId[]");
                var impulse = Require(item, "ApplyImpulse", true, voidType, item, "System.Single",
                    "UnityEngine.Vector3", "System.Single");
                var register = Require(item, "Register", false, integer);
                var stockAllocate = Require(item, "AllocateAllComponents", false, voidType, "Vector3l", "UnityEngine.Quaternion");
                var allocate = Require(item, "AllocateAllComponents", false, voidType, "Manager.SkyTransformData",
                    boolean, boolean, "System.Single", integer, boolean);
                var release = Require(item, "ReleaseComponentsAndItemId", false, voidType, boolean);
                var releaseAll = Require(item, "ReleaseAllComponents", false, voidType, boolean);
                var material = Require(voxel, "get_MainTexId", false, integer);
                Require(item, "CanRenderInWorld", false, boolean);
                Require(loop, "get_IsMainThread", true, boolean);
                Require(loop, "get_IsNativeContainerAccessUnsafe", true, boolean);
                Require("PlayerState", "get_InLoadingScreen", true, boolean);
                Require("PlayerState", "get_IsQuittingToDesktopOrTitle", true, boolean);
                Require("PlayerState", "get_sessionMode", true, "PlayerState/SessionMode");
                Require("Game.ItemInstanceCommon", "IsValidStackQuantity", true, boolean, "System.Single");
                Require("Common.RNG.SkeetoHash", ".ctor", false, voidType, "System.UInt32");
                Require("Common.RNG.SkeetoHash", "GetRotation", false, "UnityEngine.Quaternion");
                if (!drop.IsPrivate || !drop.HasBody || drop.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute")
                    || !Field(module, item, "seed", "System.UInt32", false)
                    || !Field(module, item, "disposeTime", "System.Single", false)
                    || !Field(module, "Manager.SkyTransformData", "systemPosition", "Vector3l", false)
                    || !Field(module, "Manager.SkyTransformData", "rotation", "UnityEngine.Quaternion", false)
                    || new[] { "id", "rigidbodyId", "worldColliderId", "transformId" }
                        .Any(field => !Field(module, item, field, integer, false)))
                    throw new InvalidOperationException("Terrain mining or native world item fields changed.");

                GatheringCall(edit, drop);
                var instructions = drop.Body.Instructions;
                int spawnAt = GatheringCall(drop, spawn);
                int impulseAt = GatheringCall(drop, impulse);
                int materialAt = GatheringCall(drop, material);
                if (spawnAt < 4 || impulseAt <= spawnAt + 2 || materialAt >= spawnAt
                    || instructions[spawnAt + 1].OpCode != OpCodes.Pop
                    || (instructions[spawnAt - 4].OpCode != OpCodes.Ldloca && instructions[spawnAt - 4].OpCode != OpCodes.Ldloca_S)
                    || instructions[spawnAt - 4].Operand is not VariableDefinition local || local.VariableType.FullName != item
                    || !LoadsMiningLocal(instructions[spawnAt + 2], local))
                    throw new InvalidOperationException("The stock impulse no longer receives the item value filled by the mining spawn.");
                if (GatheringCall(spawn, register) >= GatheringCall(spawn, stockAllocate))
                    throw new InvalidOperationException("Stock mining spawns no longer register before allocating components.");
                GatheringCall(stockAllocate, allocate);
                GatheringCall(release, releaseAll);
                return new CapabilityResult(capability, true, "Managed terrain drop call and stock native item impulse validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool LoadsMiningLocal(Instruction instruction, VariableDefinition expected)
        {
            return instruction.OpCode.Code switch
            {
                Code.Ldloc or Code.Ldloc_S => ReferenceEquals(instruction.Operand, expected),
                Code.Ldloc_0 => expected.Index == 0,
                Code.Ldloc_1 => expected.Index == 1,
                Code.Ldloc_2 => expected.Index == 2,
                Code.Ldloc_3 => expected.Index == 3,
                _ => false
            };
        }
    }
}
