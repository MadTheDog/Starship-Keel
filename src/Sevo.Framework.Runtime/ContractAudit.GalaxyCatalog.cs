using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateGalaxyCatalogPreview(ModuleDefinition module)
        {
            const string capability = "galaxyCatalogPreview";
            const string star = "World.StarData";
            const string vector = "UnityEngine.Vector3";
            const string catalog = "World.GalaxyStarDistribution";
            const string routes = "World.GalaxyWarpGateRoutes";
            const string shaderStar = "World.GalaxyCommon/ShaderStarData";
            const string shaderRoute = "World.GalaxyCommon/ShaderWarpGateRouteData";
            try
            {
                if (!Field(module, "Manager.AssetRepoGalaxy", "Distribution", catalog, true)
                    || !Field(module, "Manager.AssetRepoGalaxy", "WarpGateRoutes", routes, true)
                    || !Field(module, "Manager.GameIntroductionResources", "STARTING_SYSTEM", star, true)
                    || !Field(module, catalog, "StarDatas", "System.Collections.Generic.List`1<" + star + ">", false)
                    || !Field(module, catalog, "StarShaderDatas", shaderStar + "[]", false)
                    || !Field(module, catalog, "StarSeedPointers", "System.Int32[]", false)
                    || !Field(module, catalog, "StarSeedLength", "System.Int32[]", false)
                    || !Field(module, routes, "StartToEnd", "System.Collections.Generic.Dictionary`2<" + star + "," + star + ">", false))
                    throw new InvalidOperationException("Stock catalog, route or starting system bindings changed.");

                if (!Field(module, star, "seed", "System.UInt64", false)
                    || !Field(module, star, "sector", "Vector3i", false)
                    || !Field(module, star, "size", "System.Single", false)
                    || !Field(module, star, "color", "UnityEngine.Color", false)
                    || !GalaxyReturns(Method(module, star, ".ctor", false, "System.Void", "System.UInt64", "Vector3i"))
                    || !GalaxyReturns(Method(module, star, "get_Coordinate", false, "Vector3i"))
                    || !GalaxyReturns(Method(module, star, "get_HolomapPosition", false, vector))
                    || !GalaxyReturns(Method(module, star, "SetEconomyScores", false, "System.Void", "System.Int32[]")))
                    throw new InvalidOperationException("Native star identity, positions or economy contract changed.");

                foreach (var name in new[] { "size", "agricultural", "industrial", "mining", "highTech", "tradeHub", "frontier", "tourism" })
                    if (!Field(module, shaderStar, name, "System.Single", false))
                        throw new InvalidOperationException("Native galaxy shader star row field changed: " + name);
                if (!Field(module, shaderStar, "position", vector, false)
                    || !Field(module, shaderStar, "color", "UnityEngine.Vector4", false)
                    || !Field(module, shaderRoute, "startPosition", vector, false)
                    || !Field(module, shaderRoute, "endPosition", vector, false)
                    || !Field(module, shaderRoute, "isDiscovered", "System.Int32", false))
                    throw new InvalidOperationException("Native galaxy shader position, color or gate bindings changed.");

                return new CapabilityResult(capability, true, "Stock catalog and native candidate preview contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool GalaxyReturns(MethodDefinition? method) => method?.IsPublic == true && method.HasBody
            && method.Body.Instructions.LastOrDefault()?.OpCode.Code == Code.Ret;
    }
}
