using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluatePersistentWaypointMap(ModuleDefinition module)
        {
            const string name = "persistentWaypointMap";
            const string map = "World.SystemMap";
            const string holomap = "UI.UI_Holomap";
            const string vector = "UnityEngine.Vector3";
            const string camera = "UnityEngine.Camera";
            const string projection = "UI.UI_Common";
            const string rect = "UnityEngine.RectTransform";
            var widget = Method(module, "UI.Galaxy.UI_HolomapEntityWidget", "Update", false, "System.Void");
            var selectPlanet = Method(module, map, "SetSelectedPlanet", false, "System.Void", rect, "World.SolarBody");
            var awake = Method(module, map, "Awake", false, "System.Void");
            var setActive = Method(module, map, "SetActive", false, "System.Void");
            bool available = UniqueFields(module, map, ("holomapCamera", camera), ("selectedPlanetWidget", rect))
                && Field(module, map, "IsActive", "Common.BoolEventVariable", true)
                && Field(module, map, "IsOrreryModeActive", "Common.BoolEventVariable", true)
                && Field(module, map, "currentSystem", "World.SolarBodyVariable", true)
                && Field(module, map, "planetHover", "World.SolarBodyVariable", true)
                && HasCall(Method(module, holomap, "Update", false, "System.Void"), map, "OnUpdate")
                && setActive?.HasBody == true && awake?.HasBody == true
                && awake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld
                    && i.Operand is FieldReference active && active.DeclaringType.FullName == map && active.Name == "IsActive"
                    && i.Next?.Next?.OpCode == OpCodes.Ldftn && i.Next.Next.Operand == setActive
                    && i.Next.Next.Next?.Next?.Operand is MethodReference subscribe && subscribe.Name == "add_OnValueChanged")
                && Method(module, map, "OnDestroy", false, "System.Void")?.HasBody == true
                && HasCall(selectPlanet, projection, "GetWidgetPosition")
                && HasCall(selectPlanet, rect, "set_anchoredPosition")
                && Method(module, projection, "GetWidgetPosition", true, vector, camera, vector, vector)?.IsPublic == true
                && Method(module, "Vector3l", "ToVector3", false, vector)?.IsPublic == true
                && HasCall(widget, projection, "GetWidgetPosition")
                && widget!.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4
                    && (float)i.Operand == PersistentWaypointMap.OrreryScale
                    && i.Next?.Next?.Operand is MethodReference convert && convert.DeclaringType.FullName == "Vector3l"
                    && convert.Name == "ToVector3"
                    && i.Next.Next.Next?.Operand is MethodReference multiply && multiply.DeclaringType.FullName == vector
                    && multiply.Name == "op_Multiply");
            return new CapabilityResult(name, available, available
                ? "Native system map lifecycle, Orrery scale and widget projection validated."
                : "Native system map lifecycle, Orrery scale or widget projection changed.");

            static bool HasCall(MethodDefinition? method, string type, string methodName)
                => method?.HasBody == true && method.Body.Instructions.Any(i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                    && i.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == methodName);

            static bool UniqueFields(ModuleDefinition target, string type, params (string Name, string Type)[] fields)
                => target.GetType(type) is { } owner && fields.All(spec => owner.Fields.Count(field =>
                    field.Name == spec.Name && field.FieldType.FullName == spec.Type && !field.IsStatic) == 1);
        }

        public static CapabilityResult EvaluatePersistentWaypoints(ModuleDefinition module)
        {
            const string name = "persistentWaypointStorage";
            const string save = "Manager.Save.PlayerSave_mp";
            const string star = "World.StarData";
            const string body = "World.SolarBody";
            const string transform = "Manager.SkyTransformData";
            const string transforms = "Manager.SkyTransformDataContainer";
            const string grid = "VoxelEngine.ParentEntity";
            bool available = Field(module, "SaveManager", "playerSave", save, true)
                && Method(module, "SaveManager", "get_CharacterName", true, "System.String")?.IsPublic == true
                && Method(module, save, "get_seed", false, "System.UInt64")?.IsPublic == true
                && Field(module, save, "Load_Legacy", "System.Boolean", true)
                && Field(module, "World.PlanetarySystem", "CurrentStarData", star, true)
                && Field(module, "World.PlanetarySystem", "InSystemTransition", "System.Boolean", true)
                && Field(module, star, "seed", "System.UInt64", false)
                && Field(module, "World.PlanetarySystem", "SolarBodies", "System.Collections.Generic.List`1<" + body + ">", true)
                && Field(module, body, "parentStar", star, false)
                && Field(module, body, "planetGenerationId", "System.Int32", false)
                && Field(module, body, "transformId", "System.Int32", false)
                && Method(module, body, "get_Seed", false, "System.UInt64")?.IsPublic == true
                && Method(module, body, "get_AstronType", false, "World.AstronType")?.IsPublic == true
                && Method(module, "BlockEntityManager", "GetEntityBySGUID", false, grid, "System.String")?.IsPublic == true
                && Field(module, grid, "SGUID", "System.String", false)
                && Method(module, "VoxelEngine.VoxelEntity", "get_WillBeUnspawned", false, "System.Boolean")?.IsPublic == true
                && Method(module, "VoxelEngine.VoxelEntity", "get_SkyTransform", false, "SkyTransform")?.IsPublic == true
                && Method(module, "SkyTransform", "get_CurrentSystem", false, star)?.IsPublic == true
                && Method(module, star, "Equals", false, "System.Boolean", star)?.IsPublic == true
                && Field(module, transform, "datas", transforms, true)
                && Method(module, transforms, "get_IsCreated", false, "System.Boolean")?.IsPublic == true
                && Method(module, transforms, "get_Length", false, "System.Int32")?.IsPublic == true
                && Method(module, transforms, "get_Item", false, transform, "System.Int32")?.IsPublic == true
                && Method(module, transform, "LocalToSystem", false, "Vector3l", "UnityEngine.Vector3")?.IsPublic == true
                && Method(module, "Vector3l", ".ctor", false, "System.Void", "System.Int64", "System.Int64", "System.Int64")?.IsPublic == true;
            return new CapabilityResult(name, available, available
                ? "Native save identity and body, grid, and system waypoint inputs validated."
                : "Native save identity or waypoint anchor inputs changed.");
        }
    }
}
