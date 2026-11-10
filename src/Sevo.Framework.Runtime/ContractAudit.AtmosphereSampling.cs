using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateAtmospherePositionSampling(ModuleDefinition module)
        {
            const string name = "atmospherePositionSampling";
            const string transform = "Manager.SkyTransformData";
            const string transforms = "Manager.SkyTransformDataContainer";
            bool available = Field(module, transform, "datas", transforms, true)
                && Method(module, transforms, "get_IsCreated", false, "System.Boolean")?.IsPublic == true
                && Method(module, transforms, "get_Length", false, "System.Int32")?.IsPublic == true
                && Method(module, transforms, "get_Item", false, transform, "System.Int32")?.IsPublic == true
                && Method(module, transform, "SystemToLocal", false, "UnityEngine.Vector3", "Vector3l")?.IsPublic == true;
            return new CapabilityResult(name, available, available
                ? "Native loaded body transform and system-to-local conversion validated."
                : "Native loaded body transform inputs changed.");
        }

        public static CapabilityResult EvaluateAtmosphereSampling(ModuleDefinition module)
        {
            const string capability = "atmosphereSamplingInputs";
            const string row = "World.Terrain.Planet.PlanetGravityData";
            const string container = "Common.NativeRecyclableContainer`1";
            const string rows = container + "<" + row + ">";
            const string transform = "Manager.SkyTransformData";
            const string planet = "World.Terrain.Planet.PlanetCommon";
            const string number = "System.Single";
            const string body = "World.SolarBody";
            const string system = "World.PlanetarySystem";
            const string star = "World.StarData";

            try
            {
                foreach (string name in new[] { "radius", "oceanRadius", "atmosphereDensity" })
                    if (!Field(module, row, name, number, false))
                        throw new InvalidOperationException("Native atmosphere row field changed: " + name);

                if (!Field(module, row, "transformId", "System.Int32", false)
                    || !Field(module, row, "planetGenerationId", "System.Int32", false)
                    || !Field(module, "World.Terrain.Planet.PlanetResources", "PlanetGravityDatas", rows, true))
                    throw new InvalidOperationException("Native body and gravity row indexing changed.");
                if (Method(module, container, "get_IsCreated", false, "System.Boolean")?.IsPublic != true
                    || Method(module, container, "get_Length", false, "System.Int32")?.IsPublic != true
                    || Method(module, container, "get_Item", false, "T", "System.Int32")?.IsPublic != true)
                    throw new InvalidOperationException("Native gravity row reads changed.");
                foreach (string name in new[] { "gravityId", "transformId", "planetGenerationId" })
                    if (!Field(module, body, name, "System.Int32", false))
                        throw new InvalidOperationException("Native body index changed: " + name);

                if (!Field(module, body, "parentStar", star, false)
                    || !Field(module, star, "seed", "System.UInt64", false)
                    || !Field(module, system, "CurrentStarData", star, true)
                    || !Field(module, system, "InSystemTransition", "System.Boolean", true)
                    || !Field(module, system, "SolarBodies", "System.Collections.Generic.List`1<" + body + ">", true)
                    || Method(module, body, "get_Seed", false, "System.UInt64")?.IsPublic != true
                    || Method(module, body, "get_AstronType", false, "World.AstronType")?.IsPublic != true)
                    throw new InvalidOperationException("Native seeded body lookup changed.");

                foreach (var guard in new[] { "get_IsMainThread", "get_IsNativeContainerAccessUnsafe" })
                    if (Method(module, "Manager.GameloopManager", guard, true, "System.Boolean")?.IsPublic != true)
                        throw new InvalidOperationException("Native atmosphere read phase guard changed: " + guard);
                foreach (var guard in new[] { "get_InLoadingScreen", "get_IsQuittingToDesktopOrTitle" })
                    if (Method(module, "PlayerState", guard, true, "System.Boolean")?.IsPublic != true)
                        throw new InvalidOperationException("Native world lifetime guard changed: " + guard);
                if (Method(module, "PlayerState", "get_sessionMode", true, "PlayerState/SessionMode")?.IsPublic != true)
                    throw new InvalidOperationException("Native session mode lookup changed.");
                RequireSurfaceEnum(module, "PlayerState/SessionMode", "solo", (int)PlayerState.SessionMode.solo);
                RequireSurfaceEnum(module, "World.AstronType", "telluric", (int)World.AstronType.telluric);

                var falloff = Method(module, planet, "GetAtmosphereFalloffFactor", true, number, number, number);
                if (falloff == null || !falloff.HasBody)
                    throw new InvalidOperationException("Native atmosphere falloff is unavailable.");

                var toLocal = Method(module, transform, "SystemToLocal", false, "UnityEngine.Vector3", "Vector3l");
                if (toLocal?.IsPublic != true || !toLocal.HasBody)
                    throw new InvalidOperationException("Native system-to-body position conversion is unavailable.");

                var publish = Method(module, body, "SetPhysicsData", false, "System.Void");
                if (publish == null || !publish.HasBody)
                    throw new InvalidOperationException("Native gravity row publication is unavailable.");
                int density = Stores(publish, row, "atmosphereDensity", 1).Single();
                int write = publish.Body.Instructions.IndexOf(publish.Body.Instructions.Single(instruction =>
                    CallsBodyMethod(instruction, rows, "set_Item", 2)));
                if (density >= write || !publish.Body.Instructions.Take(density).Any(instruction =>
                    instruction.OpCode.Code == Code.Ldfld && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == body && field.Name == "gravityId")
                    || !publish.Body.Instructions.Take(density).Any(instruction =>
                    instruction.OpCode.Code == Code.Ldfld && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == "World.Terrain.PlanetDensityGenerationData"
                    && field.Name == "atmosphereDensity"))
                    throw new InvalidOperationException("Generated atmosphere density no longer feeds the native gravity row.");

                return new CapabilityResult(capability, true, "Native density, body coordinate and atmosphere falloff inputs validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
