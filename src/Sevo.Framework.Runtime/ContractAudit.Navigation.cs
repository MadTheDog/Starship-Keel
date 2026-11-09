using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateNavigationMarkers(ModuleDefinition module)
        {
            const string capability = "navigationMarkers";
            const string hud = "UI.UI_MainWaypoints";
            const string widget = "UI.UI_WaypointWidget";
            const string waypoint = hud + "/WaypointData";
            const string vector = "UnityEngine.Vector3";
            try
            {
                var update = NavMethod(module, hud, "LateUpdate", false, "System.Void");
                NavMethod(module, hud, "OnDestroy", false, "System.Void");
                var draw = NavMethod(module, widget, "OnUpdate", false, "System.Void", waypoint, "UnityEngine.Camera", vector);
                var hide = NavMethod(module, widget, "SetActive", false, "System.Void", "System.Boolean");
                var world = NavMethod(module, waypoint, "get_WorldPosition", false, vector);
                var template = module.GetType(hud)?.Fields.SingleOrDefault(field => field.Name == "waypointTemplate"
                    && !field.IsStatic && field.FieldType.FullName == "UnityEngine.GameObject");
                if (template == null || !draw.IsPublic || !hide.IsPublic
                    || !NavCalls(update, "OnUpdate", widget) || !NavCalls(draw, "get_WorldPosition", waypoint)
                    || !NavCalls(world, "LocalToWorld", "Manager.SkyTransformData"))
                    throw new InvalidOperationException("Stock HUD waypoint widget or coordinate contract changed.");
                return new CapabilityResult(capability, true, "Stock HUD widget and entity-relative coordinate contract validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateNavigationTargets(ModuleDefinition module)
        {
            const string capability = "navigationTargets";
            const string hud = "UI.HUD_TargetManager";
            const string tracking = "UI.TrackingData";
            const string ship = "Spaceship";
            const string intType = "System.Int32";
            const string voidType = "System.Void";
            try
            {
                var select = NavMethod(module, hud, "OnNewHolomapTarget", true, voidType, intType, ship);
                var route = NavMethod(module, hud, "SetCurrentSelected", true, voidType, intType, ship);
                var apply = NavMethod(module, hud, "ApplyTargetToShip", true, voidType, ship, tracking);
                var present = NavMethod(module, hud, "PresentTargetInHud", true, voidType, ship, intType, tracking);
                var entity = NavMethod(module, tracking, "GetEntity", false, "VoxelEngine.ParentEntity");
                NavMethod(module, hud, "get_CurrentTargetId", true, intType);
                NavMethod(module, "BaseVehicle", "get_IsControlledByLocalPlayer", false, "System.Boolean");
                NavMethod(module, "VoxelEngine.ParentEntity", "get_TrackingDataId", false, intType);
                if (!select.IsPublic || !entity.IsPublic || !NavCalls(select, "SetCurrentSelected", hud)
                    || !NavCalls(route, "ApplyTargetToShip", hud) || !NavCalls(route, "PresentTargetInHud", hud)
                    || !NavCalls(apply, "SetNewTrackedData", "VoxelEngine.EntityTargetingSystem")
                    || !NavCalls(apply, "CmdSetNewTrackedObject") || !NavCalls(present, "OnIsSelected", tracking))
                    throw new InvalidOperationException("Native holomap target selection route changed.");
                return new CapabilityResult(capability, true, "Stock holomap target selection, ship application and HUD presentation validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static MethodDefinition NavMethod(ModuleDefinition module, string owner, string name, bool isStatic,
            string result, params string[] parameters)
            => module.GetType(owner)?.Methods.SingleOrDefault(method => method.Name == name && method.IsStatic == isStatic
                && method.HasBody && method.ReturnType.FullName == result
                && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                ?? throw new InvalidOperationException("Missing native navigation method: " + owner + "." + name);

        private static bool NavCalls(MethodDefinition method, string name, string? owner = null)
            => method.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference called && called.Name == name
                && (owner == null || called.DeclaringType.FullName == owner));
    }
}
