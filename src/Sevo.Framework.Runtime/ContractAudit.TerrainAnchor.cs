using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateTerrainAnchorAttachments(ModuleDefinition module)
        {
            const string name = "terrainAnchorAttachments";
            const string grid = "VoxelEngine.ParentEntity";
            const string entity = "VoxelEngine.VoxelEntity";
            const string identity = "VoxelEngine.EntityIdentity";
            const string save = "Manager.Save.PlayerSave_mp";
            const string star = "World.StarData";
            bool available = EvaluateTerrainAnchors(module).Available
                && Field(module, "SaveManager", "playerSave", save, true)
                && Field(module, save, "Load_Legacy", "System.Boolean", true)
                && Method(module, save, "get_seed", false, "System.UInt64")?.IsPublic == true
                && Method(module, "SaveManager", "get_CharacterName", true, "System.String")?.IsPublic == true
                && Field(module, grid, "SGUID", "System.String", false)
                && Field(module, grid, "saveInWorld", "System.Boolean", false)
                && Field(module, "BlockEntityManager", "instance", "BlockEntityManager", true)
                && Method(module, "BlockEntityManager", "GetEntityBySGUID", false, grid, "System.String")?.IsPublic == true
                && Method(module, grid, "get_Identity", false, identity)?.IsPublic == true
                && Method(module, identity, "get_IsPlayerOwned", false, "System.Boolean")?.IsPublic == true
                && Method(module, entity, "get_IsInitialized", false, "System.Boolean")?.IsPublic == true
                && Method(module, entity, "get_WillBeUnspawned", false, "System.Boolean")?.IsPublic == true
                && Method(module, entity, "get_SkyTransform", false, "SkyTransform")?.IsPublic == true
                && Method(module, "SkyTransform", "get_CurrentSystem", false, star)?.IsPublic == true
                && Method(module, star, "Equals", false, "System.Boolean", star)?.IsPublic == true;
            return new CapabilityResult(name, available, available
                ? "Native save scope, saved grid identity, and body-relative contact inputs validated."
                : "Native save scope, saved grid identity, or contact inputs changed.");
        }

        public static CapabilityResult EvaluateTerrainAnchors(ModuleDefinition module)
        {
            const string capability = "terrainAnchorContacts";
            const string body = "World.SolarBody";
            const string star = "World.StarData";
            const string node = "World.Terrain.OctreeNodeData";
            const string transform = "Manager.SkyTransformData";
            const string transforms = "Manager.SkyTransformDataContainer";
            const string container = "Common.NativeRecyclableContainer`1";
            const string hit = "VoxelEngine.VoxelRaycastHit";
            const string integer = "System.Int32";
            const string vector = "UnityEngine.Vector3";
            bool available = EvaluateWorldSurfaceQuery(module).Available
                && Field(module, "World.PlanetarySystem", "CurrentStarData", star, true)
                && Field(module, "World.PlanetarySystem", "InSystemTransition", "System.Boolean", true)
                && Field(module, "World.PlanetarySystem", "SolarBodies", "System.Collections.Generic.List`1<" + body + ">", true)
                && Field(module, body, "parentStar", star, false)
                && Field(module, star, "seed", "System.UInt64", false)
                && Field(module, body, "planetGenerationId", integer, false)
                && Field(module, body, "transformId", integer, false)
                && Public(body, "get_Seed", false, "System.UInt64")
                && Public(body, "get_AstronType", false, "World.AstronType")
                && Field(module, transform, "datas", transforms, true)
                && Field(module, transform, "rotation", "UnityEngine.Quaternion", false)
                && Field(module, transform, "scale", "System.Single", false)
                && Public(transforms, "get_IsCreated", false, "System.Boolean")
                && Public(transforms, "get_Length", false, integer)
                && Public(transforms, "get_Item", false, transform, integer)
                && Public(transform, "SystemToLocal", false, vector, "Vector3l")
                && Public(transform, "LocalToSystem", false, "Vector3l", vector)
                && Public("PositionManager", "ToWorldPosition", true, vector, "Vector3l")
                && Field(module, hit, "elementId", integer, false)
                && Field(module, hit, "transformId", integer, false)
                && Public("PositionManager", "ToSystemPosition", true, "Vector3l", vector)
                && Field(module, "World.Terrain.OctreeNodeResources", "OctreeNodeDatas", container + "<" + node + ">", true)
                && Public(container, "get_IsCreated", false, "System.Boolean")
                && Public(container, "get_Length", false, integer)
                && Public(container, "get_Item", false, "T", integer)
                && Field(module, node, "transformId", integer, false)
                && Field(module, node, "planetGenerationId", integer, false)
                && Field(module, node, "asteroidId", integer, false)
                && Field(module, node, "densityComputed", "System.Boolean", false)
                && Field(module, node, "isJobStarted", "System.Boolean", false)
                && Field(module, node, "isGeoDirty", "System.Boolean", false)
                && Field(module, "World.Terrain.OctreeNodeJobs", "toGeoCompute", "System.Collections.Generic.List`1<System.Int32>", true)
                && Field(module, node, "geoState", "World.Terrain.NodeGeoState", false)
                && Field(module, node, "fadeState", "World.Terrain.NodeFadeState", false);
            return new CapabilityResult(capability, available, available
                ? "Native ray hit identity and body-relative contact transforms validated."
                : "Native ray hit identity or body-relative contact transforms changed.");

            bool Public(string type, string name, bool isStatic, string result, params string[] parameters)
                => Method(module, type, name, isStatic, result, parameters)?.IsPublic == true;
        }
    }
}
