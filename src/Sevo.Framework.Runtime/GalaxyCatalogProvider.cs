using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Common.Rendering;
using Common.RNG;
using Common.SkyApplication;
using Game;
using Game.Player;
using HarmonyLib;
using Manager;
using Manager.Save;
using Sevo.Framework.Sdk;
using UI;
using UI.MainScreen;
using Unity.Mathematics;
using UnityEngine;
using World.Starfield;

namespace Sevo.Framework.Runtime
{
    /// <summary>Declares one reproducible galaxy for a marked, managed solo save root.</summary>
    public static class GalaxyCatalogProvider
    {
        private sealed class Declaration
        {
            internal readonly string Key;
            internal readonly int Revision;
            internal readonly Func<GalaxyCatalogPlan> Build;

            internal Declaration(string key, int revision, Func<GalaxyCatalogPlan> build)
            {
                Key = key;
                Revision = revision;
                Build = build;
            }
        }

        private static readonly Dictionary<string, Declaration> declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        private static ProfileSession? profile;
        private static Action<string>? warn;
        private static Harmony? guards;
        private static Harmony? consumers;
        private static bool sealedCatalog;
        private static GalaxyCatalogPublication? active;
        private static FileStream? sessionLease;

        public static bool IsAvailable { get; private set; }
        internal static bool IsPublished => active != null && sessionLease != null;

        /// <summary>Call from the dependent plugin's Awake. Build runs once before native character entry.</summary>
        public static void Register(string ownerId, string localName, int revision, Func<GalaxyCatalogPlan> build)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
            if (build == null) throw new ArgumentNullException(nameof(build));
            if (sealedCatalog) throw new InvalidOperationException("Galaxy catalog declaration collection is closed; restart required.");
            if (declarations.ContainsKey(key)) throw new InvalidOperationException("Duplicate galaxy catalog declaration: " + key);
            declarations.Add(key, new Declaration(key, revision, build));
        }

        internal static CapabilityResult Install(ProfileSession? session, CapabilityResult contract, bool requested, Action<string> warning)
        {
            profile = session;
            warn = warning;
            guards = new Harmony(Plugin.Id + ".galaxyCatalog.guards");
            try
            {
                Patch(guards, typeof(UI_CharacterCreation), "PlayWithNewCharacter", nameof(BeforeUnavailableEntry));
                Patch(guards, typeof(UI_CharacterSlot), "PressButton", nameof(BeforeUnavailableEntry));
                Patch(guards, typeof(UI_LobbySlot), "ConnectToLobby", nameof(BeforeLobby));
                Patch(guards, typeof(UI_MultiplayerLobby), "ToggleLobby", nameof(BeforeLobby));
            }
            catch (Exception exception)
            {
                // Keep any installed UI refusal. If the UI shape changed, a known save boundary
                // can still abort before entering the native host and writing the character.
                string failure = "Galaxy catalog entry guards could not be installed: " + exception.Message;
                try
                {
                    Patch(guards, typeof(SaveManager), "NewCharacter", nameof(RequireSupportedSaveEntry),
                        typeof(string), typeof(string), typeof(PlayerState.GameMode), typeof(SkinData));
                    Patch(guards, typeof(SaveManager), "SetCharacter", nameof(RequireSupportedSaveEntry),
                        typeof(string), typeof(PlayerSave_mp));
                }
                catch (Exception recovery)
                {
                    failure += ". The native save refusal boundary is also unavailable: " + recovery.Message;
                }
                return new CapabilityResult(contract.Name, false, failure);
            }
            if (!contract.Available) return contract;
            if (!requested || session == null)
                return new CapabilityResult(contract.Name, false, "Galaxy catalog publication requires opt-in and a managed solo profile.");

            consumers = new Harmony(Plugin.Id + ".galaxyCatalog.consumers");
            try
            {
                PatchEntryBody(consumers, typeof(UI_CharacterCreation), "PlayWithNewCharacter", nameof(InjectCreate));
                PatchEntryBody(consumers, typeof(UI_CharacterSlot), "PressButton", nameof(InjectSelect));
                Patch(consumers, typeof(FactionTerritoryMap), "SpawnFortress", nameof(BeforeSpawnFortress),
                    typeof(LongHash).MakeByRefType(), typeof(int));
                Patch(consumers, typeof(StarfieldParticle), "InitParticle", nameof(BeforeInitParticle));
                IsAvailable = true;
                return contract;
            }
            catch (Exception exception)
            {
                consumers.UnpatchSelf();
                consumers = null;
                return new CapabilityResult(contract.Name, false, "Galaxy catalog consumer hooks could not be installed: " + exception.Message);
            }
        }

        internal static void Disable()
        {
            IsAvailable = false;
            if (active != null) return;
            consumers?.UnpatchSelf();
            // A disabled publication capability must still protect marked or pinned saves.
        }

        private static void Patch(Harmony harmony, Type target, string method, string prefix, params Type[] parameters)
        {
            var entry = Boundary(target, method, parameters);
            harmony.Patch(entry, prefix: new HarmonyMethod(typeof(GalaxyCatalogProvider), prefix) { priority = Priority.Last });
        }

        private static MethodInfo Boundary(Type target, string method, params Type[] parameters)
        {
            var entry = AccessTools.DeclaredMethod(target, method, parameters);
            if (entry == null || entry.ReturnType != typeof(void))
                throw new InvalidOperationException("Missing native galaxy boundary: " + target.FullName + "." + method);
            return entry;
        }

        private static void PatchEntryBody(Harmony harmony, Type target, string method, string injector)
        {
            var entry = Boundary(target, method);
            if (entry.IsStatic || entry.GetMethodBody()?.ExceptionHandlingClauses.Count != 0)
                throw new InvalidOperationException("The native character entry body is not suitable for the galaxy admission gate.");
            harmony.Patch(entry, transpiler: new HarmonyMethod(typeof(GalaxyCatalogProvider), injector) { priority = Priority.Last });
        }

        private static IEnumerable<CodeInstruction> InjectCreate(IEnumerable<CodeInstruction> source, ILGenerator generator)
            => InjectEntryGate(source, generator, nameof(BeforeCreate));

        private static IEnumerable<CodeInstruction> InjectSelect(IEnumerable<CodeInstruction> source, ILGenerator generator)
            => InjectEntryGate(source, generator, nameof(BeforeSelect));

        private static IEnumerable<CodeInstruction> InjectEntryGate(IEnumerable<CodeInstruction> source, ILGenerator generator, string gate)
        {
            var body = source.ToList();
            if (body.Count == 0 || body.Any(instruction => instruction.blocks.Count != 0))
                throw new InvalidOperationException("The native character entry IL has an unexpected exception region.");

            // This runs in the original body, after HarmonyX has resolved every prefix's cancellation.
            var start = generator.DefineLabel();
            var labels = AccessTools.Field(typeof(CodeInstruction), "labels")?.GetValue(body[0]) as System.Collections.IList
                ?? throw new InvalidOperationException("The Harmony entry label collection is unavailable.");
            labels.Add(start);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(GalaxyCatalogProvider), gate));
            yield return new CodeInstruction(OpCodes.Brtrue, start);
            yield return new CodeInstruction(OpCodes.Ret);
            foreach (var instruction in body) yield return instruction;
        }

        private static bool BeforeUnavailableEntry()
        {
            if (IsAvailable) return true;
            try { return !RequiresGuardedRoot() || Refuse("This save root requires an available and enabled custom galaxy catalog."); }
            catch (Exception exception) { return Refuse(exception.Message); }
        }

        private static void RequireSupportedSaveEntry()
        {
            if (!RequiresGuardedRoot()) return;
            const string reason = "The native UI galaxy admission boundary is unavailable for this marked save root.";
            Refuse(reason);
            throw new InvalidOperationException(reason);
        }

        private static bool BeforeCreate(object __instance)
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.mainMenu) return true;
            return Admit(creating: true, () =>
            {
                if (ReadField(typeof(UI_CharacterCreation), __instance, "isInvalid") is not false)
                    throw new InvalidOperationException("The native character creation form has invalid data.");
                string name = ReadInput(__instance, "input");
                string seed = ReadInput(__instance, "inputSeed");
                return (name, LongHash.StringToSeed(seed), (FileInfo?)null, (PlayerSave_mp?)null);
            });
        }

        private static string ReadInput(object owner, string field)
        {
            var input = ReadField(typeof(UI_CharacterCreation), owner, field);
            return input?.GetType().GetProperty("text")?.GetValue(input) as string
                ?? throw new InvalidOperationException("The native character input is unavailable: " + field);
        }

        private static object? ReadField(Type type, object owner, string field)
            => AccessTools.DeclaredField(type, field)?.GetValue(owner);

        private static bool BeforeSelect(object __instance)
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.mainMenu) return true;
            return Admit(creating: false, () =>
            {
                var slot = typeof(UI_CharacterSlot);
                if (ReadField(slot, __instance, "invalidSave") is not false
                    || ReadField(slot, __instance, "playerSave") is not PlayerSave_mp save
                    || ReadField(slot, __instance, "fileInfo") is not FileInfo file
                    || ReadField(slot, __instance, "characterName") is not string name
                    || !string.Equals(name, save.characterName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The selected native character has no verified save identity.");
                return (name, save.seed, file, save);
            });
        }

        private static bool Admit(bool creating, Func<(string Name, ulong Seed, FileInfo? File, PlayerSave_mp? Save)> selection)
        {
            FileStream? lease = null;
            try
            {
                string root = Paths.SAVE_CHARACTER;
                var pin = profile?.ReadGalaxyCatalogPin();
                if (active == null && !GalaxyCatalogNativeSaveBinding.HasMarker(root) && pin?.UsesSaveRoot(root) != true) return true;
                if (active != null)
                    throw new InvalidOperationException("Restart the game before entering another character in a custom galaxy.");
                if (!IsAvailable || profile == null)
                    throw new InvalidOperationException("This save root requires an available and enabled custom galaxy catalog.");
                if (sealedCatalog)
                    throw new InvalidOperationException("Restart before retrying a rejected galaxy catalog declaration.");
                sealedCatalog = true;
                if (declarations.Count != 1)
                    throw new InvalidOperationException("This save root requires exactly one galaxy declaration. Found " +
                        (declarations.Count == 0 ? "none." : string.Join(", ", declarations.Keys.OrderBy(key => key, StringComparer.Ordinal)) + "."));

                var character = selection();
                lease = profile.AcquireGalaxyCatalogSession();
                pin = profile.ReadGalaxyCatalogPin();
                GalaxyCatalogNativeSaveBinding.Verify(profile, root, requireEmpty: pin == null);
                var declaration = declarations.Values.Single();
                var plan = declaration.Build() ?? throw new InvalidOperationException("The galaxy declaration returned no catalog.");
                var candidate = new GalaxyCatalogPublication(GalaxyCatalogPreview.Compile(plan));
                var admitted = GalaxyCatalogPreEntry.VerifySelection(profile, root, pin, declaration.Key, declaration.Revision,
                    candidate.Fingerprint, character.Name, character.Seed, creating, character.File?.FullName);
                if (character.Save is { } save && !candidate.Contains(save.starData))
                    throw new InvalidOperationException("The saved current system is absent from the pinned galaxy.");

                var sources = GalaxyCatalogPreview.AuditPreEntrySource();
                if (!sources.Available) throw new InvalidOperationException(sources.Reason);
                if (FactionTerritoryMap.fortressLevelBuffer != null || FactionTerritoryMap.computeBuffer != null
                    || StarfieldParticle.instance != null)
                    throw new InvalidOperationException("Native galaxy consumers are already initialized. Restart before custom galaxy entry.");
                if (creating) profile.WriteGalaxyCatalogPin(admitted);
                candidate.Publish();
                active = candidate;
                sessionLease = lease;
                lease = null;
                warn?.Invoke("Published galaxy " + declaration.Key + " with " + plan.Stars.Count + " stars and " + plan.Routes.Count + " gate routes.");
                return true;
            }
            catch (Exception exception)
            {
                return Refuse(exception.Message);
            }
            finally { lease?.Dispose(); }
        }

        private static bool BeforeLobby()
        {
            try
            {
                if (RequiresGuardedRoot())
                    return Refuse("A custom galaxy save root is limited to solo play.");
                return true;
            }
            catch (Exception exception)
            {
                return Refuse(exception.Message);
            }
        }

        private static bool RequiresGuardedRoot()
        {
            string root = Paths.SAVE_CHARACTER;
            return active != null || (!string.IsNullOrWhiteSpace(root)
                && (GalaxyCatalogNativeSaveBinding.HasMarker(root) || profile?.ReadGalaxyCatalogPin()?.UsesSaveRoot(root) == true));
        }

        private static bool Refuse(string reason)
        {
            string message = "Keel blocked galaxy entry. " + reason;
            warn?.Invoke(message);
            try { UI_ConfirmationWindow.OpenWindow(message); }
            catch (Exception exception) { warn?.Invoke("The galaxy refusal dialog could not open: " + exception.Message); }
            return false;
        }

        private static bool BeforeSpawnFortress(ref LongHash hash, int factionId, List<int2> ___sectorControlled,
            FactionTerritoryMap.FactionSectorOwnership[,] ___SectorOwnerships, List<int> ___fortressToSpawn)
        {
            if (active == null) return true;
            var catalog = AssetRepoGalaxy.Distribution;
            var unused = new List<int>();
            foreach (var sector in ___sectorControlled)
            {
                int index = sector.x + GalaxyCatalogPlan.SectorWidth * sector.y;
                int first = catalog.StarSeedPointers[index];
                int count = catalog.StarSeedLength[index];
                unused.Clear();
                for (int star = first; star < first + count; star++)
                    if (!FactionTerritoryMap.FortressLocations.ContainsKey(catalog.StarDatas[star])) unused.Add(star);

                FactionTerritoryMap.PopulateFortressToSpawn(___SectorOwnerships[sector.x, sector.y].strengthLevel, ref hash);
                foreach (int level in ___fortressToSpawn)
                {
                    if (unused.Count == 0) break;
                    int slot = hash.GetIntRange(0, unused.Count - 1);
                    int star = unused[slot];
                    unused[slot] = unused[unused.Count - 1];
                    unused.RemoveAt(unused.Count - 1);
                    FactionTerritoryMap.FortressLocations.Add(catalog.StarDatas[star], new FactionTerritoryMap.FortressData
                    {
                        fortressLevel = level,
                        factionId = factionId,
                        starDataIndex = star,
                    });
                }
            }
            return false;
        }

        private static bool BeforeInitParticle(ref ProceduralParticle ___particle, List<Vector4> ___positions,
            List<Vector4> ___sizes, List<Vector4> ___colors)
        {
            if (active == null || ___positions.Count != 0) return true;
            ___particle?.ReleaseAllBuffers();
            ___particle = null!;
            ___sizes.Clear();
            ___colors.Clear();
            return false;
        }
    }
}
