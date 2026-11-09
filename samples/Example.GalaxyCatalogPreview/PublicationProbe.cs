using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Common.RNG;
using Common.SkyApplication;
using Game;
using Game.Player;
using HarmonyLib;
using Manager;
using Manager.Save;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UI;
using UnityEngine;
using UnityEngine.UI;
using World;
using World.Galaxy;

namespace Sevo.Example.GalaxyPreviewProbe
{
    public sealed partial class Plugin
    {
        private const string TestOwner = "example.galaxy-publication-probe";
        private const string TestCharacter = "KeelGalaxyProbe";
        private const string TestSeed = "11062645";
        private const ulong AuthoredStarSeed = 987654321;
        private string? publicationMode;
        private string? publicationAction;
        private string? publicationState;
        private bool publicationStarted;
        private bool publicationFailed;
        private string publicationStage = "title";
        private int publicationBuilds;
        private readonly Stopwatch publicationClock = new Stopwatch();
        private static Plugin? publicationHarnessOwner;
        private Harmony? publicationHarness;
        private string? publicationSaveRoot;
        private bool reportedTutorialGuard;
        private PlayerHealth? initialRespawnHealth;
        private int initialRespawnFrame;

        private void SetUpPublicationProbe()
        {
            publicationMode = Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_PUBLICATION_PROBE");
            if (string.IsNullOrEmpty(publicationMode)) return;
            try
            {
                Require(Application.companyName == "SEVO Keel Gen Lab 0110" && Application.productName == "SEVO WG Test",
                    "The publication probe runs only in the disposable Keel generation lab.");
                var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                publicationState = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                Require(Guid.TryParseExact(session, "N", out _) && publicationState != null, "A managed launcher session is required.");
                ProfileProtocol.Open(publicationState!, BepInEx.Paths.GameRootPath, session);
                publicationAction = Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_PUBLICATION_ACTION") ?? "create";
                Require(publicationAction is "create" or "load" or "reject-create" or "reject-load" or "cancel-create"
                    or "disable-create" or "stock-create",
                    "Unknown publication probe action.");
                Require(publicationMode is "authored" or "subset" or "mismatch" or "missing" or "conflict", "Unknown publication probe catalog.");
                if (publicationMode != "missing")
                    GalaxyCatalogProvider.Register(TestOwner, "catalog", 1, BuildPublicationCatalog);
                if (publicationMode == "conflict")
                    GalaxyCatalogProvider.Register(TestOwner, "conflict", 1, BuildPublicationCatalog);
                publicationClock.Start();
            }
            catch (Exception error) { FailPublication(error); }
        }

        private GalaxyCatalogPlan BuildPublicationCatalog()
        {
            publicationBuilds++;
            var intro = GameIntroductionResources.STARTING_SYSTEM;
            var start = new GalaxyStarSeed(intro.seed, intro.sector.x, intro.sector.y, intro.sector.z);
            if (publicationMode == "subset")
            {
                var stockTarget = WarpCommon.GetWarpgateTarget(intro);
                return GalaxyCatalogPreview.SelectStockSubset(star => star.Seed == stockTarget.seed ||
                    (star.SectorX == start.SectorX && star.SectorZ == start.SectorZ));
            }
            var authored = new GalaxyStarSeed(publicationMode == "mismatch" ? AuthoredStarSeed + 1 : AuthoredStarSeed, 1, 1, 1);
            return new GalaxyCatalogPlan(new[] { authored, start }, new[] { new GalaxyGateRoute(start.Seed, authored.Seed) }, start);
        }

        private void UpdatePublicationProbe()
        {
            if (string.IsNullOrEmpty(publicationMode) || publicationStarted) return;
            if (publicationClock.Elapsed > TimeSpan.FromMinutes(2))
            {
                FailPublication(new TimeoutException("waiting for the stock title screen"));
                return;
            }
            if (PlayerState.sessionMode != PlayerState.SessionMode.mainMenu || UI_TitleScreen.instance == null
                || UI_TitleScreen.IsActive?.Value != true || SkinDataCommon.RaceVariants is not { Length: > 0 }) return;
            publicationStarted = true;
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_PROBE_DRAIN") == "1")
                StartCoroutine(DrainLabFrame());
            StartCoroutine(GuardPublication());
        }

        private IEnumerator GuardPublication()
        {
            var run = RunPublication();
            while (!publicationFailed)
            {
                object? next;
                try
                {
                    if (publicationClock.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("galaxy publication " + publicationStage);
                    if (!run.MoveNext()) yield break;
                    next = run.Current;
                }
                catch (Exception error) { FailPublication(error); yield break; }
                yield return next;
            }
        }

        private IEnumerator RunPublication()
        {
            bool cancel = publicationAction == "cancel-create";
            bool disable = publicationAction == "disable-create";
            bool stock = publicationAction == "stock-create";
            bool create = publicationAction is "create" or "reject-create" || cancel || disable || stock;
            bool reject = publicationAction is "reject-create" or "reject-load" || cancel || disable;
            string root = Path.GetFullPath(Path.Combine(publicationState!, "galaxy-characters"));
            Require(Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0,
                "The disposable galaxy character root is unavailable.");
            if (stock)
                Require(publicationMode == "missing" && !Directory.EnumerateFileSystemEntries(root).Any(),
                    "The stock fallback fixture requires an empty unmarked disposable root and no provider.");
            else
                Require(File.Exists(Path.Combine(root, "sevo-galaxy-save-root.json")), "The disposable galaxy save marker is required.");
            Paths.SAVE_CHARACTER = root;
            StartPublicationHarness(root, gameplayDiagnostics: !reject);
            publicationStage = "stock-character-entry";
            string file = PlayerSave_mp.GetPath(TestCharacter);
            byte[]? before = File.Exists(file) ? File.ReadAllBytes(file) : null;
            Require(create == (before == null), "The probe save state does not match the requested action.");
            string pinPath = Path.Combine(publicationState!, "galaxy-catalog-pin.json");
            string leasePath = Path.Combine(publicationState!, "galaxy-catalog-session.lock");
            var stockRows = AssetRepoGalaxy.Distribution.StarShaderDatas;
            var stockRoutes = AssetRepoGalaxy.WarpGateRoutes.StartToEnd;
            if (cancel || disable)
                Require(publicationMode == "authored" && !File.Exists(pinPath) && !File.Exists(leasePath) && GalaxyCatalogProvider.IsAvailable,
                    "The guard fixture requires a fresh authored profile with publication enabled.");
            if (disable)
            {
                var stop = typeof(GalaxyCatalogProvider).GetMethod("Disable", BindingFlags.Static | BindingFlags.NonPublic);
                Require(stop != null, "The publication teardown entry is unavailable.");
                stop!.Invoke(null, null);
                Require(!GalaxyCatalogProvider.IsAvailable, "The publication teardown did not disable publication.");
            }

            UI_TitleScreen.instance.ShowPlayScreen();
            yield return null;
            var picker = UnityEngine.Object.FindAnyObjectByType<UI_CharacterSelection>();
            Require(picker != null && UI_CharacterSelection.IsActive.Value, "Stock character selection did not open.");
            if (create)
            {
                picker!.ShowCharacterCreationWindow();
                yield return null;
                var form = UnityEngine.Object.FindAnyObjectByType<UI_CharacterCreation>();
                Require(form != null && UI_CharacterCreation.IsActive.Value, "Stock character creation did not open.");
                Read<InputField>(form!, "input").text = TestCharacter;
                Read<InputField>(form!, "inputSeed").text = TestSeed;
                form!.OnInput();
                form.OnSeedUpdated();
                if (!Read<bool>(form, "isCreative")) form.OnGameModeButton();
                Require(!Read<bool>(form, "isInvalid"), "The stock character form rejected the fixture.");
                if (cancel)
                {
                    var harmony = new Harmony(DiagnosticOwner + ".cancellation");
                    harmony.Patch(AccessTools.DeclaredMethod(typeof(UI_CharacterCreation), nameof(UI_CharacterCreation.PlayWithNewCharacter)),
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(CancelStockCreation)) { priority = Priority.Last - 100 });
                    try { form.PlayWithNewCharacter(); }
                    finally { harmony.UnpatchSelf(); }
                }
                else form.PlayWithNewCharacter();
            }
            else
            {
                publicationStage = "stock-character-picker";
                UI_CharacterSlot? slot = null;
                float pickerDeadline = Time.realtimeSinceStartup + 15f;
                while (slot == null && Time.realtimeSinceStartup < pickerDeadline)
                {
                    var matches = picker!.GetComponentsInChildren<UI_CharacterSlot>(false)
                        .Where(item => string.Equals(item.gameObject.name, TestCharacter, StringComparison.Ordinal)
                            && string.Equals(Path.GetFullPath(Read<FileInfo>(item, "fileInfo").FullName), file,
                                StringComparison.OrdinalIgnoreCase)).ToArray();
                    Require(matches.Length <= 1, "The stock picker created duplicate slots for the exact saved character.");
                    slot = matches.SingleOrDefault();
                    if (slot == null) yield return null;
                }
                Require(slot != null, "The stock picker did not create the exact slot for the verified native character file.");
                Logger.LogInfo($"KEEL_GALAXY_PUBLICATION_PROBE selected stock character={TestCharacter}");
                slot!.PressButton();
            }

            yield return null;
            if (reject)
            {
                Require(PlayerState.sessionMode == PlayerState.SessionMode.mainMenu, "Rejected entry left the stock title session.");
                Require(before == null ? !Directory.Exists(Path.GetDirectoryName(file))
                    : File.Exists(file) && before.SequenceEqual(File.ReadAllBytes(file)), "Rejected entry wrote the native character save.");
                if (cancel || disable)
                    Require(publicationBuilds == 0 && !File.Exists(pinPath) && !File.Exists(leasePath)
                        && ReferenceEquals(stockRows, AssetRepoGalaxy.Distribution.StarShaderDatas)
                        && ReferenceEquals(stockRoutes, AssetRepoGalaxy.WarpGateRoutes.StartToEnd),
                        "A cancelled or disabled stock entry changed galaxy state.");
                Logger.LogInfo($"KEEL_GALAXY_PUBLICATION_PROBE PASS action={publicationAction} catalog={publicationMode} nativeSaveUnchanged=true");
                yield break;
            }

            publicationStage = "initial-system-and-player";
            while (PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.InLoadingScreen
                || LoadManager.AreJobRemaining || PlanetarySystem.InSystemTransition || PlanetarySystem.currentSystem == null
                || Dependency.PlayerManager == null || Dependency.PlayerManager.Health == null || GameloopManager.jobStarted
                || Dependency.PlayerManager.Health.IsDead || Dependency.PlayerManager.Health.IsRespawning
                || (create && (!ReferenceEquals(Dependency.PlayerManager.Health, initialRespawnHealth)
                    || Time.frameCount <= initialRespawnFrame || GameIntroductionResources.instance?.Medbay?.inAnimation != false)))
                yield return null;

            var catalog = AssetRepoGalaxy.Distribution;
            var intro = GameIntroductionResources.STARTING_SYSTEM;
            Require(catalog.StarDatas.Count > 1 && catalog.StarShaderDatas.Length == catalog.StarDatas.Count
                && StarData.GetBySeed(intro.seed) == intro, "The candidate star and shader tables were not coherent.");
            if (stock)
                Require(ReferenceEquals(stockRows, catalog.StarShaderDatas) && ReferenceEquals(stockRoutes, AssetRepoGalaxy.WarpGateRoutes.StartToEnd)
                    && !File.Exists(pinPath) && !File.Exists(leasePath), "Unmarked stock entry changed galaxy state.");
            var routes = AssetRepoGalaxy.WarpGateRoutes;
            Require(routes.StartToEnd.Count == routes.EndToStart.Count && routes.StartToEnd.Count == routes.WarpGateRouteDistances.Count,
                "The published gate dictionaries and distances do not describe the same routes.");
            var maps = Resources.FindObjectsOfTypeAll<GalaxyMap>();
            const BindingFlags privateFields = BindingFlags.Instance | BindingFlags.NonPublic;
            var mapCatalog = typeof(GalaxyMap).GetField("starDistribution", privateFields);
            var mapRenderer = typeof(GalaxyMap).GetField("starsRenderer", privateFields);
            var starBuffer = typeof(HolomapStarRenderer).GetField("starsBuffer", privateFields);
            var buffers = maps.Select(map => mapRenderer?.GetValue(map)).Where(renderer => renderer != null)
                .Select(renderer => starBuffer?.GetValue(renderer) as ComputeBuffer).ToArray();
            Require(maps.Length > 0 && maps.All(map => ReferenceEquals(mapCatalog?.GetValue(map), catalog))
                && buffers.Length > 0 && buffers.All(buffer => buffer?.count == catalog.StarDatas.Count),
                "The stock holomap has no initialized renderer with the published catalog buffer.");
            Require(FactionTerritoryMap.FortressLocations.All(pair => pair.Value.starDataIndex >= 0
                && pair.Value.starDataIndex < catalog.StarDatas.Count && catalog.StarDatas[pair.Value.starDataIndex] == pair.Key),
                "A native fortress references a different catalog index.");
            var target = WarpCommon.GetWarpgateTarget(intro);
            if (publicationMode == "authored")
                Require(catalog.StarDatas.Count == 2 && target.seed == AuthoredStarSeed
                    && StarData.GetBySeed(AuthoredStarSeed) == target && WarpCommon.GetWarpgateTarget(target) == intro,
                    "The native route did not resolve both authored endpoints.");
            if (publicationMode == "subset")
                Require(target.IsValid && WarpCommon.GetWarpgateTarget(target) == intro,
                    "The native route did not preserve the starting star's stock gate pair.");

            publicationStage = "native-save";
            while (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe || GameloopManager.jobStarted)
                yield return null;
            Require(IsPublicationCharacter() && string.Equals(PlayerSave_mp.GetPath(TestCharacter), file, StringComparison.Ordinal),
                "The native character save path changed after the managed redirect.");
            if (create) SaveManager.SaveOnSceneQuit();
            var saved = PlayerSave_mp.LoadFromDisk(file);
            Require(saved != null && saved.characterName == TestCharacter && saved.seed == LongHash.StringToSeed(TestSeed)
                && saved.starData == PlanetarySystem.CurrentStarData, "Stock save/reload did not preserve the character and system identities.");
            ObserveBindings(catalog, "published-system");
            Logger.LogInfo($"KEEL_GALAXY_PUBLICATION_PROBE PASS action={publicationAction} catalog={publicationMode}"
                + $" stars={catalog.StarDatas.Count} routes={AssetRepoGalaxy.WarpGateRoutes.StartToEnd.Count} current={saved!.starData.seed}");
        }

        private void StartPublicationHarness(string root, bool gameplayDiagnostics)
        {
            publicationSaveRoot = root;
            publicationHarnessOwner = this;
            publicationHarness = new Harmony(DiagnosticOwner + ".stock-respawn");
            publicationHarness.Patch(AccessTools.DeclaredMethod(typeof(Paths), nameof(Paths.Initialize), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterNativePathInitialization)) { priority = Priority.Last });
            if (!gameplayDiagnostics) return;
            var tutorial = AccessTools.DeclaredMethod(typeof(GameIntroductionResources.GameIntroductionLogic),
                "UpdateCreativeTutorial", Type.EmptyTypes) ?? throw new MissingMethodException("The stock Creative tutorial seam is missing.");
            publicationHarness.Patch(tutorial, prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowCreativeTutorial)));
            publicationHarness.Patch(AccessTools.DeclaredMethod(typeof(PlayerHealth), nameof(PlayerHealth.OnRespawnFinished), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterStockRespawn)));
        }

        private void StopPublicationHarness()
        {
            publicationHarness?.UnpatchSelf();
            if (publicationHarnessOwner == this) publicationHarnessOwner = null;
        }

        private bool IsPublicationCharacter() => SaveManager.CharacterName == TestCharacter
            && publicationSaveRoot != null && string.Equals(Paths.SAVE_CHARACTER, publicationSaveRoot, StringComparison.Ordinal)
            && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative;

        private static void AfterNativePathInitialization()
        {
            var owner = publicationHarnessOwner;
            if (owner?.publicationSaveRoot is not { } root || string.Equals(Paths.SAVE_CHARACTER, root, StringComparison.Ordinal)) return;
            Require(Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0
                && (owner.publicationAction == "stock-create" || File.Exists(Path.Combine(root, "sevo-galaxy-save-root.json"))),
                "The managed galaxy character root became unavailable during native path initialization.");
            Paths.SAVE_CHARACTER = root;
            owner.Logger.LogInfo("KEEL_GALAXY_PUBLICATION_PROBE restored the disposable character root after native path initialization");
        }

        private static bool AllowCreativeTutorial(GameIntroductionResources.GameIntroductionLogic __instance)
        {
            var owner = publicationHarnessOwner;
            if (__instance.missionPhase != 0 || owner == null) return true;
            bool defer = owner.IsPublicationCharacter();
            if (!owner.reportedTutorialGuard)
            {
                owner.reportedTutorialGuard = true;
                owner.Logger.LogInfo($"KEEL_GALAXY_PUBLICATION_PROBE tutorial deferred={defer} character={SaveManager.CharacterName}"
                    + $" rootMatches={string.Equals(Paths.SAVE_CHARACTER, owner.publicationSaveRoot, StringComparison.Ordinal)}"
                    + $" session={PlayerState.sessionMode} mode={PlayerState.gameMode}");
            }
            return !defer;
        }

        private static void AfterStockRespawn(PlayerHealth __instance)
        {
            var owner = publicationHarnessOwner;
            if (owner == null || !owner.IsPublicationCharacter()
                || !GameIntroductionResources.IsStartingSystem(PlanetarySystem.CurrentStarData)
                || GameIntroductionResources.instance?.Medbay == null || !ReferenceEquals(Dependency.PlayerManager?.Health, __instance)) return;
            owner.initialRespawnHealth = __instance;
            owner.initialRespawnFrame = Time.frameCount;
            owner.Logger.LogInfo($"KEEL_GALAXY_PUBLICATION_PROBE stock initial respawn completed frame={Time.frameCount}");
        }

        private IEnumerator DrainLabFrame()
        {
            var phase = typeof(GameloopManager).GetField("jobFrameState", BindingFlags.Static | BindingFlags.NonPublic);
            if (phase == null || !phase.FieldType.IsEnum || !Enum.IsDefined(phase.FieldType, "JobsScheduled"))
            {
                FailPublication(new InvalidOperationException("The lab frame diagnostic cannot identify native job scheduling."));
                yield break;
            }
            var scheduled = Enum.Parse(phase.FieldType, "JobsScheduled");
            var end = new WaitForEndOfFrame();
            var stalled = new Stopwatch();
            while (!PlayerState.IsQuittingToDesktopOrTitle)
            {
                yield return end;
                var graph = JobGraph.Main;
                if (!GameloopManager.IsMainThread || !Equals(phase.GetValue(null), scheduled)
                    || PlayerState.sessionMode != PlayerState.SessionMode.solo || graph.CurrentFrame != Time.frameCount
                    || graph.ScheduledNodesThisFrame.Count == 0)
                {
                    stalled.Reset();
                    continue;
                }
                stalled.Start();
                if (stalled.ElapsedMilliseconds < 500) continue;
                try { GameloopManager.OnAfterRendering(); }
                catch (Exception error)
                {
                    FailPublication(new InvalidOperationException("The lab frame diagnostic failed.", error));
                    yield break;
                }
            }
        }

        private static T Read<T>(object owner, string name) =>
            (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
                ?? throw new InvalidOperationException("Missing stock UI field: " + name));

        private static bool CancelStockCreation() => false;

        private static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
        }

        private void FailPublication(Exception error)
        {
            publicationStarted = true;
            publicationFailed = true;
            Logger.LogError("KEEL_GALAXY_PUBLICATION_PROBE FAIL " + error);
        }
    }
}
