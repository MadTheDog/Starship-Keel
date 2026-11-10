using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks source identity and respawn routes. This audit does not admit a claim mutation.</summary>
        public static CapabilityResult EvaluateStockWreckSources(ModuleDefinition module)
        {
            const string capability = "stockWreckSourceInputs";
            const string entities = "World.WorldEntities";
            const string node = "World.Terrain.OctreeNodeData";
            const string saved = "Manager.Save.TerrainEntityInstance_mp";
            const string grid = "VoxelEngine.ParentEntity";
            const string toolbox = "Game.MissionToolbox";
            const string star = "World.StarData";
            const string list = "System.Collections.Generic.List`1<" + saved + ">";
            try
            {
                var identity = Method(module, entities, "GetWreckSGUID", true, "System.String", node);
                var spawn = Method(module, entities, "SpawnWreckOnNewTerrainNode", true, "System.Void", node, "System.Int32");
                var load = Method(module, entities, "OnNodeLoad", true, "System.Void", node, "System.Int32", list);
                var save = Method(module, entities, "OnNodesave", true, "System.Void", node, "System.Int32", list);
                var unload = Method(module, entities, "OnNodeUnload", true, "System.Void", "System.Int32");
                var attach = Method(module, entities, "OnEntitySpawnedAsTerrain", true, "System.Void", grid, "System.Int32");
                var loaded = Method(module, entities, "OnTerrainWreckLoaded", true, "System.Void", grid);
                var savedLoader = Method(module, toolbox, "LoadEntityFromPlayerInstanceAtPath", true, "System.Boolean",
                    "System.String", "WorldTransform", star, grid + "&", "VoxelEngine.BuildPermission");
                var root = Method(module, "World.SolarBody", "InitializePlanetGenerationData", false, "System.Void");
                var expand = Method(module, "World.Terrain.OctreeNodeJobs/Job_OctreeUpdate", "Expand", false, "System.Void",
                    node + "&", "System.Int32");
                var callback = loaded?.CustomAttributes.SingleOrDefault(attribute => attribute.AttributeType.FullName
                    == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
                var stateMachine = callback?.ConstructorArguments.Single().Value as TypeReference;
                var continuation = stateMachine == null ? null : Method(module, stateMachine.FullName,
                    "MoveNext", false, "System.Void");
                var identityCode = identity?.Body?.Instructions;
                var identityCalls = identityCode?.Where(i => i.OpCode.Code == Code.Ldfld).Select(i => ((FieldReference)i.Operand).FullName);
                var savedCalls = load?.Body?.Instructions.Where(i => i.OpCode.Code == Code.Call
                    && i.Operand is MethodReference method && method.FullName == savedLoader?.FullName).ToArray();
                var branch = savedCalls is { Length: 1 } ? savedCalls[0].Next : null;
                var voxelSize = spawn?.Body?.Instructions.SingleOrDefault(i => i.OpCode.Code == Code.Call
                    && i.Operand is MethodReference method && method.DeclaringType.FullName == node && method.Name == "get_VoxelSize");
                if (identityCode == null || identityCode.Count != 9 || identityCode[0].OpCode.Code != Code.Ldstr
                    || (string)identityCode[0].Operand != "{0}-{1}-w"
                    || identityCalls == null || !identityCalls.SequenceEqual(new[] { "System.Int32 " + node + "::planetShaderId",
                        "System.UInt64 " + node + "::treeGuid" })
                    || !Uses(identity, "System.String", "Format", Code.Call)
                    || !Uses(spawn, node, "isDensityFromSave", Code.Ldfld)
                    || voxelSize?.Next?.OpCode.Code != Code.Ldc_R4 || (float)voxelSize.Next.Operand != 16
                    || voxelSize.Next.Next?.OpCode.Code is not (Code.Bne_Un or Code.Bne_Un_S)
                    || !Uses(spawn, node, "planetGenerationId", Code.Ldfld)
                    || !CopiesField(root, "World.SolarBody", node, "planetGenerationId")
                    || !CopiesField(expand, node, node, "planetGenerationId")
                    || !CopiesField(expand, node, node, "planetShaderId")
                    || !Uses(root, "World.SolarBody", "get_planetIdForShader", Code.Call)
                    || !Uses(root, node, "planetShaderId", Code.Stfld)
                    || !Uses(spawn, entities, "GetWreckSGUID", Code.Call)
                    || !Uses(load, entities, "GetWreckSGUID", Code.Call)
                    || !Uses(load, saved, "get_fileName", Code.Callvirt)
                    || !Uses(save, saved, "set_fileName", Code.Callvirt)
                    || !Uses(save, entities, "nodeEntities", Code.Ldsfld)
                    || !Uses(unload, entities, "nodeEntities", Code.Ldsfld)
                    || !Uses(unload, entities, "terrainEntities", Code.Ldsfld)
                    || !Uses(unload, "BlockEntityManager", "RemoveAndDestroy", Code.Callvirt)
                    || !Uses(savedLoader, "Manager.Save.EntityInstanceSystem_mp", "GetPlayerInstanceFullPath", Code.Call)
                    || savedLoader?.Body?.Instructions.Take(3).Select(i => i.OpCode.Code)
                        .SequenceEqual(new[] { Code.Ldarg_3, Code.Ldnull, Code.Stind_Ref }) != true
                    || branch?.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                    || branch.Operand is not Instruction skip
                    || branch.Next?.OpCode.Code is not (Code.Ldloca or Code.Ldloca_S)
                    || !MatchesFallback(load!, branch, skip)
                    || !Uses(spawn, toolbox, "SpawnEntity", Code.Call)
                    || !Uses(spawn, entities, "OnEntitySpawnedAsTerrain", Code.Call)
                    || !Uses(load, entities, "OnEntitySpawnedAsTerrain", Code.Call)
                    || !Uses(attach, grid, "attachedNodeId", Code.Stfld)
                    || !Uses(attach, entities, "nodeEntities", Code.Ldsfld)
                    || !Uses(attach, entities, "terrainEntities", Code.Ldsfld)
                    || !Uses(continuation, "VoxelEngine.EntityMetaData", "isWorldWreck", Code.Stfld)
                    || !Uses(continuation, grid, "SetIsDestroyed", Code.Callvirt)
                    || !Uses(continuation, grid, "attachedNodeId", Code.Ldfld)
                    || !Uses(Method(module, "World.SolarBody", "get_Seed", false, "System.UInt64"),
                        "World.AstronData", "seed", Code.Ldfld))
                    throw new InvalidOperationException("Stock wreck source identity or fresh and saved respawn routes changed.");
                return new CapabilityResult(capability, true, "Validated stock wreck identity, native attachment and both retirement interception routes.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }

            bool Uses(MethodDefinition? method, string type, string name, Code code)
                => method?.HasBody == true && method.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code == code && instruction.Operand is MemberReference member
                    && member.DeclaringType.FullName == type && member.Name == name);

            bool CopiesField(MethodDefinition? method, string source, string target, string name)
                => method?.HasBody == true && method.Body.Instructions.Any(i =>
                    i.OpCode.Code == Code.Stfld && i.Operand is FieldReference destination
                    && destination.DeclaringType.FullName == target && destination.Name == name
                    && i.Previous.OpCode.Code == Code.Ldfld && i.Previous.Operand is FieldReference origin
                    && origin.DeclaringType.FullName == source && origin.Name == name);

            bool MatchesFallback(MethodDefinition method, Instruction branch, Instruction skip)
            {
                var code = method.Body.Instructions;
                int start = code.IndexOf(branch) + 1;
                int end = code.IndexOf(skip);
                if (end <= start || skip.OpCode.Code is not (Code.Ldloc or Code.Ldloc_S)
                    || branch.Previous?.Previous?.Previous?.OpCode.Code is not (Code.Ldloca or Code.Ldloca_S)
                    || !ReferenceEquals(branch.Previous.Previous.Previous.Operand, skip.Operand)
                    || skip.Next?.OpCode.Code != Code.Ldnull || skip.Next.Next?.Operand is not MethodReference compare
                    || compare.FullName != "System.Boolean UnityEngine.Object::op_Inequality(UnityEngine.Object,UnityEngine.Object)")
                    return false;
                var fallback = code.Skip(start).Take(end - start).ToArray();
                return fallback.Any(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference m
                    && m.DeclaringType.FullName == toolbox && m.Name == "SpawnEntity")
                    && fallback.Any(i => i.OpCode.Code == Code.Ldftn && i.Operand is MethodReference m
                        && m.DeclaringType.FullName == entities && m.Name == "OnTerrainWreckLoaded");
            }
        }
    }
}
