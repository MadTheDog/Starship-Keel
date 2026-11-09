using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
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
            => entry?.IsPublic == true && entry.HasBody
                && entry.Body.Instructions.Count(instruction => IsGalaxyCall(instruction, "Mirror.NetworkManager", "StartHost")) == 1
                && GalaxyCallsInOrder(entry, ("SaveManager", nativeSave), ("Mirror.NetworkManager", "StartHost"))
                && entry.Body.Instructions.Any(instruction => IsGalaxyCall(instruction, "PlayerState", "SetSessionMode"));

        private static bool HasGalaxyField(ModuleDefinition module, string owner, string name, string type)
            => module.GetType(owner)?.Fields.Any(field => field.Name == name && !field.IsStatic && field.FieldType.FullName == type) == true;

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
