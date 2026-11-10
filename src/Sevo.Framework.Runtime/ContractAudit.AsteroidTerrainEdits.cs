using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateAsteroidTerrainEdits(ModuleDefinition module)
        {
            const string capability = "asteroidTerrainEdits";
            const string asteroid = "World.Terrain.AsteroidData";
            const string node = "World.Terrain.OctreeNodeData";
            const string resources = "World.Terrain.OctreeNodeResources";
            const string saved = "asteroidsWithSaveData";
            try
            {
                var baseAudit = EvaluateTerrainEdits(module);
                if (!baseAudit.Available) throw new InvalidOperationException(baseAudit.Reason);
                if (!Field(module, "World.Terrain.AsteroidResources", "datas",
                        "Common.NativeRecyclableContainer`1<" + asteroid + ">", true)
                    || !Field(module, asteroid, "transformId", "System.Int32", false)
                    || !Field(module, asteroid, "parentNodeId", "System.Int32", false)
                    || !Field(module, asteroid, "guid", "System.UInt64", false)
                    || !Field(module, asteroid, "isBaked", "System.Boolean", false)
                    || !Field(module, asteroid, "hasSaveData", "System.Boolean", false))
                    throw new InvalidOperationException("Native asteroid identity or save fields changed.");

                var queue = Method(module, resources, "QueueNodeSave", true, "System.Void", node + "&", "System.Int32");
                var load = Method(module, resources, "OnSystemLoad", true, "System.Void");
                var create = Method(module, "World.JobUpdateAsteroidField", "CreateNewAsteroid", false, "System.Int32",
                    "Vector3l", "UnityEngine.Quaternion", "System.UInt64", "System.Single",
                    "System.Int32", "System.Boolean", "System.UInt64", "System.Int32");
                if (!Uses(queue, node, "asteroidId", Code.Ldfld)
                    || !Uses(queue, resources, saved, Code.Ldsflda)
                    || !Uses(queue, asteroid, "hasSaveData", Code.Stfld)
                    || !Uses(load, resources, saved, Code.Ldsflda)
                    || !Uses(create, "World.JobUpdateAsteroidField", saved, Code.Ldfld)
                    || !Uses(create, asteroid, "hasSaveData", Code.Stfld))
                    throw new InvalidOperationException("Native asteroid save admission or reload publication changed.");

                return new CapabilityResult(capability, true, "Native resident asteroid identity and edited-node persistence contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }

            bool Uses(MethodDefinition? method, string type, string name, Code code)
                => method?.HasBody == true && method.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == type && field.Name == name);
        }
    }
}
