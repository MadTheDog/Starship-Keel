using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateGalaxyCatalogPublication(ModuleDefinition module)
        {
            const string capability = "galaxyCatalogPublication";
            const string routes = "World.GalaxyWarpGateRoutes";
            const string catalog = "World.GalaxyStarDistribution";
            const string star = "World.StarData";
            const string faction = "Game.FactionTerritoryMap";
            const string starfield = "World.Starfield.StarfieldParticle";
            const string particle = "Common.Rendering.ProceduralParticle";
            const string starDictionary = "System.Collections.Generic.Dictionary`2<" + star + "," + star + ">";
            try
            {
                var entry = EvaluateGalaxyCatalogPreEntry(module);
                if (!entry.Available) throw new InvalidOperationException(entry.Reason);
                var character = Method(module, "Manager.Save.PlayerSave_mp", "GetNewCharacter", true,
                    "Manager.Save.PlayerSave_mp", "System.String");
                var gateTarget = Method(module, "Game.WarpCommon", "GetWarpgateTarget", true, star, star);
                var mapLookup = Method(module, star, "GetBySeed", true, star, "System.UInt64");
                var renderRoutes = Method(module, "World.HolomapStarRenderer", "GetWarpGateRouteShaderData", true,
                    "World.GalaxyCommon/ShaderWarpGateRouteData[]");
                if (!GalaxyCallsInOrder(character, ("Common.RNG.LongHash", "StringToSeed"))
                    || Method(module, "Manager.Save.PlayerSave_mp", "get_starData", false, star)?.IsPublic != true
                    || !Field(module, star, "position", "UnityEngine.Vector3", false)
                    || !Field(module, routes, "EndToStart", starDictionary, false)
                    || !Field(module, routes, "WarpGateRouteDistances", "System.Collections.Generic.List`1<System.Single>", false)
                    || !UsesGalaxyField(gateTarget, "Manager.AssetRepoGalaxy", "WarpGateRoutes")
                    || !UsesGalaxyField(gateTarget, routes, "StartToEnd") || !UsesGalaxyField(gateTarget, routes, "EndToStart")
                    || !UsesGalaxyField(renderRoutes, routes, "StartToEnd") || !UsesGalaxyField(mapLookup, catalog, "StarDatas"))
                    throw new InvalidOperationException("Native character seed, catalog lookup, or route publication bindings changed.");

                var spawn = Method(module, faction, "SpawnFortress", true, "System.Void", "Common.RNG.LongHash&", "System.Int32");
                var populate = Method(module, faction, "PopulateFortressToSpawn", true, "System.Void", "System.Int32", "Common.RNG.LongHash&");
                const string fort = faction + "/FortressData";
                const string ownership = faction + "/FactionSectorOwnership";
                var fortInit = Method(module, faction, "InitializeComputeBuffer", true, "System.Void");
                if (!GalaxyCallsInOrder(spawn, (faction, "PopulateFortressToSpawn"), ("Common.RNG.LongHash", "GetIntRange"))
                    || !UsesGalaxyField(spawn, catalog, "StarSeedPointers") || !UsesGalaxyField(spawn, catalog, "StarSeedLength")
                    || !UsesGalaxyField(spawn, catalog, "StarDatas") || !UsesGalaxyField(populate, faction, "fortressToSpawn")
                    || !HasGalaxyStaticField(module, faction, "sectorControlled", "System.Collections.Generic.List`1<Unity.Mathematics.int2>")
                    || module.GetType(faction)?.Fields.Any(field => field.Name == "SectorOwnerships" && field.IsStatic
                        && field.FieldType is ArrayType array && array.Rank == 2 && array.ElementType.FullName == ownership) != true
                    || !HasGalaxyStaticField(module, faction, "fortressToSpawn", "System.Collections.Generic.List`1<System.Int32>")
                    || !Field(module, faction, "FortressLocations", "System.Collections.Generic.Dictionary`2<" + star + "," + fort + ">", true)
                    || !Field(module, fort, "fortressLevel", "System.Int32", false)
                    || !Field(module, fort, "factionId", "System.Int32", false)
                    || !Field(module, fort, "starDataIndex", "System.Int32", false)
                    || !Field(module, ownership, "strengthLevel", "System.Int32", false)
                    || !Field(module, faction, "computeBuffer", "UnityEngine.ComputeBuffer", true)
                    || !Field(module, faction, "fortressLevelBuffer", "UnityEngine.ComputeBuffer", true)
                    || !UsesGalaxyField(fortInit, fort, "starDataIndex"))
                    throw new InvalidOperationException("The native faction fortress allocation or buffer binding changed.");

                var particles = Method(module, starfield, "InitParticle", false, "System.Void");
                var generate = Method(module, starfield, "GenerateStars", false, "System.Void", "System.Object");
                if (!Field(module, starfield, "instance", starfield, true)
                    || !HasGalaxyField(module, starfield, "particle", particle)
                    || !new[] { "positions", "sizes", "colors" }.All(name =>
                        HasGalaxyField(module, starfield, name, "System.Collections.Generic.List`1<UnityEngine.Vector4>"))
                    || !GalaxyCallsInOrder(particles, (particle, "ReleaseAllBuffers"), (particle, "InitBuffer"))
                    || !UsesGalaxyField(generate, catalog, "StarDatas"))
                    throw new InvalidOperationException("The native starfield buffer initialization changed.");

                return new CapabilityResult(capability, true, "Native galaxy publication, sparse catalog and world entry contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool UsesGalaxyField(MethodDefinition? method, string owner, string name)
            => method?.HasBody == true && method.Body.Instructions.Any(instruction =>
                instruction.Operand is FieldReference field && field.DeclaringType.FullName == owner && field.Name == name);

        public static CapabilityResult EvaluateGalaxyCatalogPreEntry(ModuleDefinition module)
        {
            const string capability = "galaxyCatalogPreEntry";
            const string creator = "UI.UI_CharacterCreation";
            const string slot = "UI.UI_CharacterSlot";
            const string save = "Manager.Save.PlayerSave_mp";
            try
            {
                var preview = EvaluateGalaxyCatalogPreview(module);
                if (!preview.Available) throw new InvalidOperationException(preview.Reason);
                GalaxyCatalogAssetContract.DiscoverSerializedFields(module);

                var create = Method(module, creator, "PlayWithNewCharacter", false, "System.Void");
                var select = Method(module, slot, "PressButton", false, "System.Void");
                if (!RequireGalaxyEntry(create, "NewCharacter") || !RequireGalaxyEntry(select, "SetCharacter")
                    || !HasGalaxyField(module, creator, "input", "UnityEngine.UI.InputField")
                    || !HasGalaxyField(module, creator, "inputSeed", "UnityEngine.UI.InputField")
                    || !HasGalaxyField(module, creator, "isInvalid", "System.Boolean")
                    || !HasGalaxyField(module, slot, "invalidSave", "System.Boolean")
                    || !HasGalaxyField(module, slot, "characterName", "System.String")
                    || !HasGalaxyField(module, slot, "playerSave", save)
                    || !HasGalaxyField(module, slot, "fileInfo", "System.IO.FileInfo")
                    || Method(module, save, "get_characterName", false, "System.String")?.IsPublic != true
                    || Method(module, save, "get_seed", false, "System.UInt64")?.IsPublic != true
                    || !Field(module, "Common.SkyApplication.Paths", "SAVE_CHARACTER", "System.String", true))
                    throw new InvalidOperationException("Native new or selected character entry and save identity changed.");

                var path = Method(module, save, "GetPath", true, "System.String", "System.String");
                var paths = path?.HasBody == true ? path.Body.Instructions : null;
                if (paths == null || !paths.Any(instruction => instruction.OpCode == OpCodes.Ldsfld
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == "Common.SkyApplication.Paths"
                        && field.Name == "SAVE_CHARACTER")
                    || !paths.Any(instruction => instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, ".mpc")))
                    throw new InvalidOperationException("The native character save path changed.");

                var join = GalaxyAsyncBody(module, "UI.UI_LobbySlot", "ConnectToLobby");
                var lobby = GalaxyAsyncBody(module, "UI.MainScreen.UI_MultiplayerLobby", "ToggleLobby");
                if (!GalaxyCallsInOrder(join, ("PlayerState", "SetSessionMode"), ("Manager.SteamLobbyManager", "JoinFriendsLobby"),
                        ("SaveManager", "JoinServer"), ("Mirror.NetworkManager", "StartClient"))
                    || !GalaxyCallsInOrder(lobby, ("Mirror.Transport", "ServerStart"), ("Manager.SteamLobbyManager", "OpenLobby"),
                        ("PlayerState", "SetSessionMode")))
                    throw new InvalidOperationException("The native lobby join or solo-to-host transition changed.");

                var setup = Method(module, "World.Galaxy.GalaxyMap", "Awake", false, "System.Void");
                if (setup?.HasBody != true || !setup.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Newobj
                    && instruction.Operand is MethodReference method && method.DeclaringType.FullName == "World.HolomapStarRenderer"))
                    throw new InvalidOperationException("The native galaxy map renderer initialization changed.");

                return new CapabilityResult(capability, true, "Stock entry, save identity and serialized galaxy field contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool RequireGalaxyEntry(MethodDefinition? entry, string nativeSave)
            => entry?.IsPublic == true && entry.HasBody && !entry.Body.HasExceptionHandlers
                && entry.Body.Instructions.Count(instruction => IsGalaxyCall(instruction, "Mirror.NetworkManager", "StartHost")) == 1
                && GalaxyCallsInOrder(entry, ("SaveManager", nativeSave), ("Mirror.NetworkManager", "StartHost"))
                && entry.Body.Instructions.Any(instruction => IsGalaxyCall(instruction, "PlayerState", "SetSessionMode"));

        private static bool HasGalaxyField(ModuleDefinition module, string owner, string name, string type)
            => module.GetType(owner)?.Fields.Any(field => field.Name == name && !field.IsStatic && field.FieldType.FullName == type) == true;

        private static bool HasGalaxyStaticField(ModuleDefinition module, string owner, string name, string type)
            => module.GetType(owner)?.Fields.Any(field => field.Name == name && field.IsStatic && field.FieldType.FullName == type) == true;

        private static MethodDefinition? GalaxyAsyncBody(ModuleDefinition module, string owner, string name)
        {
            var method = Method(module, owner, name, false, "System.Void");
            var marker = method?.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
            return marker?.ConstructorArguments.Count == 1 && marker.ConstructorArguments[0].Value is TypeReference state
                ? module.GetType(state.FullName)?.Methods.SingleOrDefault(candidate => candidate.Name == "MoveNext" && candidate.HasBody)
                : null;
        }

        private static bool GalaxyCallsInOrder(MethodDefinition? method, params (string Type, string Name)[] calls)
        {
            if (method?.HasBody != true) return false;
            if (calls.Length == 0) return true;
            int next = 0;
            foreach (var instruction in method.Body.Instructions)
                if (IsGalaxyCall(instruction, calls[next].Type, calls[next].Name) && ++next == calls.Length)
                    return true;
            return false;
        }

        private static bool IsGalaxyCall(Instruction instruction, string type, string name)
            => (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name;
    }
}
