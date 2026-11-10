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
                const string rng = "Common.RNG.SkeetoHash";
                const string scalar = "System.Single";
                var seedHash = Require(rng, ".ctor", false, voidType, "System.UInt32");
                var selectPrefab = Require(rng, "GetIntRange", false, integer, integer, integer);
                var drawScale = Require(rng, "GetFloatRange", false, scalar, scalar, scalar);
                var rotate = Require(rng, "GetRotation", false, "UnityEngine.Quaternion");
                if (!drop.IsPrivate || !drop.HasBody || drop.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute")
                    || !Field(module, item, "seed", "System.UInt32", false)
                    || !Field(module, item, "disposeTime", "System.Single", false)
                    || !Field(module, item, "scale", scalar, false)
                    || !Field(module, "Manager.SkyTransformData", "systemPosition", "Vector3l", false)
                    || !Field(module, "Manager.SkyTransformData", "rotation", "UnityEngine.Quaternion", false)
                    || !Field(module, "Manager.SkyTransformData", "scale", scalar, false)
                    || !Field(module, "Manager.SkyTransformData", "localScale", scalar, false)
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
                var spawnCode = spawn.Body.Instructions;
                int seedAt = GatheringCall(spawn, seedHash);
                int selectAt = GatheringCall(spawn, selectPrefab);
                int scaleAt = GatheringCall(spawn, drawScale);
                int registerAt = GatheringCall(spawn, register);
                int rotateAt = GatheringCall(spawn, rotate);
                int allocateAt = GatheringCall(spawn, stockAllocate);
                var rngCalls = spawnCode.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                    && instruction.Operand is MethodReference method && method.DeclaringType.FullName == rng).ToArray();
                if (!rngCalls.SequenceEqual(new[] { spawnCode[seedAt], spawnCode[selectAt], spawnCode[scaleAt], spawnCode[rotateAt] })
                    || seedAt < 3 || selectAt < 7 || scaleAt < 6 || selectAt <= seedAt || scaleAt <= selectAt
                    || registerAt <= scaleAt || rotateAt <= registerAt || allocateAt != rotateAt + 1
                    || spawnCode[seedAt - 2].OpCode.Code != Code.Ldarg_0
                    || !IsMiningField(spawnCode[seedAt - 1], Code.Ldfld, item, "seed")
                    || !LoadsMiningLocalAddress(spawnCode[seedAt - 3], out var hash)
                    || !LoadsMiningLocalAddress(spawnCode[selectAt - 7], out var selectHash) || !ReferenceEquals(hash, selectHash)
                    || spawnCode[selectAt - 6].OpCode.Code != Code.Ldc_I4_0
                    || spawnCode[selectAt - 5].OpCode.Code != Code.Ldarg_3
                    || spawnCode[selectAt - 4].OpCode.Code != Code.Ldlen
                    || spawnCode[selectAt - 3].OpCode.Code != Code.Conv_I4
                    || spawnCode[selectAt - 2].OpCode.Code != Code.Ldc_I4_1
                    || spawnCode[selectAt - 1].OpCode.Code != Code.Sub
                    || spawnCode[scaleAt - 6].OpCode.Code != Code.Ldarg_0
                    || !LoadsMiningLocalAddress(spawnCode[scaleAt - 5], out var scaleHash) || !ReferenceEquals(hash, scaleHash)
                    || spawnCode[scaleAt - 4].OpCode.Code != Code.Ldarg_2
                    || !IsMiningField(spawnCode[scaleAt - 3], Code.Ldfld, "Unity.Mathematics.float2", "x")
                    || spawnCode[scaleAt - 2].OpCode.Code != Code.Ldarg_2
                    || !IsMiningField(spawnCode[scaleAt - 1], Code.Ldfld, "Unity.Mathematics.float2", "y")
                    || !IsMiningField(spawnCode[scaleAt + 1], Code.Stfld, item, "scale")
                    || !LoadsMiningLocalAddress(spawnCode[rotateAt - 1], out var rotationHash) || !ReferenceEquals(hash, rotationHash))
                    throw new InvalidOperationException("Stock mining no longer samples the seeded prefab, scale and rotation in order.");
                GatheringCall(stockAllocate, allocate);
                foreach (string field in new[] { "scale", "localScale" })
                {
                    var write = stockAllocate.Body.Instructions.Single(instruction =>
                        IsMiningField(instruction, Code.Stfld, "Manager.SkyTransformData", field));
                    if (!IsMiningField(write.Previous, Code.Ldfld, item, "scale"))
                        throw new InvalidOperationException("The stock component transform no longer starts from the item scale.");
                }
                GatheringCall(release, releaseAll);
                return new CapabilityResult(capability, true, "Managed terrain drop, seeded scale, component allocation and stock impulse validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool LoadsMiningLocalAddress(Instruction instruction, out VariableDefinition? local)
        {
            local = instruction.Operand as VariableDefinition;
            return local != null && instruction.OpCode.Code is Code.Ldloca or Code.Ldloca_S;
        }

        private static bool IsMiningField(Instruction instruction, Code opcode, string owner, string name) =>
            instruction.OpCode.Code == opcode && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == owner && field.Name == name;
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
