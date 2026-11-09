using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using Sevo.Framework.Sdk;

[assembly: InternalsVisibleTo("Sevo.Framework.Tests")]

namespace Sevo.Framework.Runtime
{
    [BepInPlugin(Id,"Starship Keel","0.1.10")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id="local.sevo.modplatform";
        private static Plugin? instance;
        private readonly RuntimeSnapshot status=new RuntimeSnapshot();
        private IReadOnlyDictionary<string,CapabilityResult> capabilities=new Dictionary<string,CapabilityResult>();
        private string[] codexMenus=Array.Empty<string>();
        private ProfileSession? profile;
        private Harmony? harmony;
        private Harmony? functionalHarmony;
        private Harmony? gateHarmony;
        private Harmony? attachmentHarmony;
        private Harmony? attachmentInteractionHarmony;
        private Harmony? attachmentVisualHarmony;
        private Harmony? receiverRepeatHarmony;
        private Harmony? modularReceiverHarmony;
        private readonly List<Action> contentHookStops = new List<Action>();
        private readonly AttachmentMaterialResources attachmentMaterials=new AttachmentMaterialResources();
        private string? materialConflict;
        private Assembly? pendingAttachmentGame;
        private CapabilityResult? pendingAttachmentCapability;
        private bool awaitingAttachmentInteraction;
        private GameAdapter? adapter;
        private NativeRegistrationSession? registration;
        private NativeLimits? limits;
        private bool experimental;
        private bool resourceChecksPassed;

        private void Awake()
        {
            instance=this;
            try
            {
                bool requested=Config.Bind("Experimental","EnableStockShapeContent",false,
                    "Owned disposable lab only. Requires a launcher session/profile marker. No world, blueprint or multiplayer safety guarantee.").Value
                    || Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT")=="1";
                bool functionalRequested=Config.Bind("Experimental","EnableFunctionalBlocks",false,
                    "Allow validated functional commands in a managed disposable solo profile.").Value
                    || Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_FUNCTIONAL")=="1";
                requested|=functionalRequested;
                string? directory=Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string? nonce=Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
                if(!string.IsNullOrWhiteSpace(directory))
                {
                    try { profile=ProfileProtocol.Open(directory!,Paths.GameRootPath,nonce ?? ""); }
                    catch(Exception exception) { Logger.LogError("Managed profile rejected; audit-only, no handshake: "+exception.Message); }
                }
                experimental=requested && profile!=null;
                status.Mode=experimental ? "experimental-stock-shapes" : "audit";
                if(requested && !experimental) Logger.LogWarning("Experimental content requires a valid managed profile/session; factories will not run.");
                var game=AppDomain.CurrentDomain.GetAssemblies().Single(assembly=>assembly.GetName().Name=="Assembly-CSharp");
                CapabilityResult functionalCapability;
                CapabilityResult gateCapability;
                CapabilityResult attachmentCapability;
                CapabilityResult visualCapability;
                CapabilityResult materialCapability;
                CapabilityResult paintCapability;
                CapabilityResult repeatCapability;
                CapabilityResult defaultPaintCapability;
                CapabilityResult modularCapability;
                CapabilityResult codexCapability;
                CapabilityResult recipes;
                CapabilityResult items;
                CapabilityResult itemVisuals;
                CapabilityResult tintedItemSurface;
                CapabilityResult machineRecipes;
                CapabilityResult gathering;
                CapabilityResult fluids;
                CapabilityResult oreGeneration;
                CapabilityResult oreScanner;
                CapabilityResult oreDiscovery;
                CapabilityResult terrainMaterials;
                CapabilityResult storageConnectors;
                CapabilityResult flushStoragePlacement;
                CapabilityResult nativeBlockVariants;
                CapabilityResult contextActions;
                CapabilityResult inventoryViews;
                CapabilityResult worldSurfaceQuery;
                CapabilityResult gridRecovery;
                CapabilityResult terrainEdits;
                CapabilityResult seatedPoseTargets;
                CapabilityResult dismountPoses;
                CapabilityResult blockAssemblies;
                CapabilityResult shipyardAssemblyEligibility;
                CapabilityResult transferLinks;
                CapabilityResult gridPhysics;
                CapabilityResult sublightControl;
                CapabilityResult cameraView;
                CapabilityResult mechanismPaths;
                CapabilityResult gridAttachments;
                CapabilityResult gridDestruction;
                CapabilityResult navigationMarkers;
                CapabilityResult navigationTargets;
                CapabilityResult logicOutputs;
                CapabilityResult vehicleControl;
                CapabilityResult waypointFlight;
                CapabilityResult bodyEnvironment;
                CapabilityResult terrainTextureOverrides;
                CapabilityResult terrainMaterialRegistration;
                CapabilityResult celestialLayout;
                bool celestialLayoutCorrectPoiOrdinal;
                CapabilityResult galaxyCatalogPreview;
                CapabilityResult galaxyCatalogPublication;
                CapabilityResult terrainPlacement;
                CapabilityResult terrainRadialPlacement;
                CapabilityResult terrainAuthoredSurfacePlacement;
                CapabilityResult terrainMiningDrops;
                CapabilityResult planetTerrainGeneration;
                CapabilityResult asteroidTerrainGeneration;
                using(var metadata=AssemblyDefinition.ReadAssembly(game.Location))
                {
                    status.AssemblyMvid=metadata.MainModule.Mvid.ToString("D");
                    capabilities=ContractAudit.Evaluate(metadata.MainModule);
                    status.ContractsValidated=capabilities.Values.All(result=>result.Name=="stockHull" || result.Available);
                    if(status.ContractsValidated) limits=ContractAudit.ReadLimits(metadata.MainModule);
                    functionalCapability=ContractAudit.EvaluateFunctionalCommands(metadata.MainModule);
                    gateCapability=ContractAudit.EvaluateFunctionalEventGate(metadata.MainModule);
                    attachmentCapability=ContractAudit.EvaluateWeaponAttachments(metadata.MainModule);
                    visualCapability=ContractAudit.EvaluateAttachmentVisuals(metadata.MainModule);
                    materialCapability=visualCapability.Available ? ContractAudit.EvaluateAttachmentMaterials(metadata.MainModule)
                        : new CapabilityResult("attachmentMaterials",false,"Native attachment visuals are unavailable.");
                    paintCapability=materialCapability.Available ? ContractAudit.EvaluateAttachmentPaint(metadata.MainModule)
                        : new CapabilityResult("attachmentPaint",false,"Native attachment materials are unavailable.");
                    repeatCapability=ContractAudit.EvaluateReceiverRepeats(metadata.MainModule);
                    defaultPaintCapability=ContractAudit.EvaluateAttachmentDefaultPaint(metadata.MainModule);
                    modularCapability=repeatCapability.Available && visualCapability.Available
                        ? ContractAudit.EvaluateModularReceiverRendering(metadata.MainModule)
                        : new CapabilityResult("modularReceiverRendering",false,"Native repeat or visual contracts are unavailable.");
                    codexCapability=ContractAudit.EvaluateCodexCategories(metadata.MainModule,out codexMenus);
                    recipes = ContractAudit.EvaluateRecipes(metadata.MainModule);
                    items = ContractAudit.EvaluateItems(metadata.MainModule);
                    itemVisuals = ContractAudit.EvaluateItemVisuals(metadata.MainModule);
                    tintedItemSurface = ContractAudit.EvaluateTintedItemSurface(metadata.MainModule);
                    machineRecipes = ContractAudit.EvaluateMachineRecipes(metadata.MainModule);
                    gathering = ContractAudit.EvaluateGathering(metadata.MainModule);
                    fluids = ContractAudit.EvaluateFluids(metadata.MainModule);
                    oreGeneration = ContractAudit.EvaluateOreGeneration(metadata.MainModule);
                    oreScanner = ContractAudit.EvaluateOreScanner(metadata.MainModule);
                    oreDiscovery = ContractAudit.EvaluateOreDiscovery(metadata.MainModule);
                    terrainMaterials = ContractAudit.EvaluateTerrainMaterialVisuals(metadata.MainModule);
                    storageConnectors = ContractAudit.EvaluateStorageConnectors(metadata.MainModule);
                    flushStoragePlacement = ContractAudit.EvaluateFlushStoragePlacement(metadata.MainModule);
                    nativeBlockVariants = ContractAudit.EvaluateNativeBlockVariants(metadata.MainModule);
                    contextActions = ContractAudit.EvaluateContextActions(metadata.MainModule);
                    inventoryViews = ContractAudit.EvaluateInventoryViews(metadata.MainModule);
                    worldSurfaceQuery = ContractAudit.EvaluateWorldSurfaceQuery(metadata.MainModule);
                    gridRecovery = ContractAudit.EvaluateGridRecovery(metadata.MainModule);
                    terrainEdits = ContractAudit.EvaluateTerrainEdits(metadata.MainModule);
                    seatedPoseTargets = ContractAudit.EvaluateSeatedPoseTargets(metadata.MainModule);
                    dismountPoses = ContractAudit.EvaluateDismountPoses(metadata.MainModule);
                    blockAssemblies = ContractAudit.EvaluateBlockAssemblies(metadata.MainModule);
                    shipyardAssemblyEligibility = ContractAudit.EvaluateShipyardAssemblyEligibility(metadata.MainModule);
                    transferLinks = ContractAudit.EvaluateTransferLinks(metadata.MainModule);
                    gridPhysics = ContractAudit.EvaluateGridPhysics(metadata.MainModule);
                    sublightControl = ContractAudit.EvaluateSublightControl(metadata.MainModule);
                    cameraView = ContractAudit.EvaluateCameraView(metadata.MainModule);
                    mechanismPaths = ContractAudit.EvaluateMechanismPaths(metadata.MainModule);
                    gridAttachments = ContractAudit.EvaluateGridAttachments(metadata.MainModule);
                    gridDestruction = ContractAudit.EvaluateGridDestruction(metadata.MainModule);
                    navigationMarkers = ContractAudit.EvaluateNavigationMarkers(metadata.MainModule);
                    navigationTargets = ContractAudit.EvaluateNavigationTargets(metadata.MainModule);
                    logicOutputs = ContractAudit.EvaluateLogicOutputs(metadata.MainModule);
                    vehicleControl = ContractAudit.EvaluateVehicleControlProvider(metadata.MainModule);
                    waypointFlight = ContractAudit.EvaluateWaypointFlight(metadata.MainModule);
                    bodyEnvironment = ContractAudit.EvaluateBodyEnvironment(metadata.MainModule);
                    terrainTextureOverrides = ContractAudit.EvaluateTerrainTextureOverrides(metadata.MainModule);
                    terrainMaterialRegistration = ContractAudit.EvaluateTerrainMaterialRegistration(metadata.MainModule);
                    celestialLayout = ContractAudit.EvaluateCelestialLayout(metadata.MainModule, out celestialLayoutCorrectPoiOrdinal);
                    galaxyCatalogPreview = ContractAudit.EvaluateGalaxyCatalogPreview(metadata.MainModule);
                    galaxyCatalogPublication = ContractAudit.EvaluateGalaxyCatalogPublication(metadata.MainModule);
                    terrainPlacement = ContractAudit.EvaluateTerrainPlacement(metadata.MainModule);
                    terrainRadialPlacement = ContractAudit.EvaluateTerrainRadialPlacement(metadata.MainModule);
                    terrainAuthoredSurfacePlacement = ContractAudit.EvaluateTerrainAuthoredSurfacePlacement(metadata.MainModule);
                    terrainMiningDrops = ContractAudit.EvaluateTerrainMiningDrops(metadata.MainModule);
                    planetTerrainGeneration = ContractAudit.EvaluatePlanetTerrainGeneration(metadata.MainModule);
                    asteroidTerrainGeneration = ContractAudit.EvaluateAsteroidTerrainGeneration(metadata.MainModule);
                    if(attachmentCapability.Available && !FunctionalStockTemplate.DiscoverReceiverTemplates(metadata.MainModule))
                        attachmentCapability=new CapabilityResult(attachmentCapability.Name,false,"No native side-slot receiver layout was found.");
                }
                foreach(var capability in capabilities.Values)
                    Logger.LogInfo(capability.Name+": "+(capability.Available ? "available — " : "unavailable — ")+capability.Reason);
                if (experimental && !status.ContractsValidated)
                {
                    experimental = false;
                    status.Mode = "audit";
                    Logger.LogWarning("Experimental brick registration disabled because its native contracts are unavailable.");
                }
                InstallBrickLifecycle(game);
                if(experimental && materialCapability.Available)
                {
                    materialCapability=InstallMaterialWorldGuard(game,materialCapability);
                    if(!materialCapability.Available)
                        paintCapability=new CapabilityResult("attachmentPaint",false,materialCapability.Reason);
                }
                if(experimental && functionalRequested && functionalCapability.Available)
                {
                    functionalCapability=InstallFunctionalCommands(game,functionalCapability);
                    if(functionalCapability.Available && gateCapability.Available) gateCapability=InstallEventGate(gateCapability);
                    if(functionalCapability.Available && attachmentCapability.Available)
                    {
                        pendingAttachmentGame=game;
                        pendingAttachmentCapability=attachmentCapability;
                    }
                }
                if(!functionalCapability.Available && attachmentCapability.Available)
                    attachmentCapability=new CapabilityResult(attachmentCapability.Name,false,"Shared functional command contracts are unavailable.");
                items = InstallContentHook(items, "EnableItemRegistration", "ITEMS",
                    () =>
                    {
                        ItemResources.Install(profile!, items, message => Logger.LogWarning(message));
                        return items;
                    },
                    () => ItemResources.Disable());
                if (itemVisuals.Available && !items.Available)
                    itemVisuals = new CapabilityResult(itemVisuals.Name, false, "Requires the item registration hook.");
                itemVisuals = InstallContentHook(itemVisuals, "EnableItemVisuals", "ITEM_VISUALS",
                    () => InstallContentHookPatches(itemVisuals, patches =>
                        ItemVisualResources.Install(patches, tintedItemSurface.Available), ItemVisualResources.Disable));
                if (tintedItemSurface.Available && !itemVisuals.Available)
                    tintedItemSurface = new CapabilityResult(tintedItemSurface.Name, false, "Requires the item visuals hook.");
                Logger.LogInfo(tintedItemSurface.Name + ": " + tintedItemSurface.Reason);
                machineRecipes = InstallContentHook(machineRecipes, "EnableMachineRecipes", "MACHINE_RECIPES",
                    () => InstallContentHookPatches(machineRecipes,
                        patches => MachineRecipeResources.Install(patches, message => Logger.LogWarning(message)),
                        MachineRecipeResources.Disable));
                if (gathering.Available && !items.Available)
                    gathering = new CapabilityResult(gathering.Name, false, "Requires the item registration hook.");
                gathering = InstallContentHook(gathering, "EnableGatheringDrops", "GATHERING",
                    () => InstallContentHookPatches(gathering, patches =>
                        GatheringRegistration.Install(patches, game, message => Logger.LogWarning(message)),
                        () => GatheringRegistration.Disable()));
                recipes = InstallContentHook(recipes, "EnableRecipeRegistration", "RECIPES",
                    () => RecipeResources.Install(profile!, recipes, message => Logger.LogWarning(message)),
                    () => RecipeResources.Disable());
                fluids = InstallContentHook(fluids, "EnableFluidRegistration", "FLUIDS",
                    () => InstallContentHookPatches(fluids, patches =>
                        FluidRegistration.Install(patches, game, profile!, message => Logger.LogWarning(message)),
                        () => FluidRegistration.Disable()));
                oreGeneration = InstallContentHook(oreGeneration, "EnableOreGeneration", "ORE_GENERATION",
                    () => InstallContentHookPatches(oreGeneration, patches =>
                        OreGeneration.Install(patches, message => Logger.LogWarning(message)),
                        () => OreGeneration.Disable()));
                oreScanner = InstallContentHook(oreScanner, "EnableOreScannerSnapshots", "ORE_SCANNER",
                    () => InstallContentHookPatches(oreScanner, patches =>
                        OreScannerSnapshots.Install(patches, message => Logger.LogWarning(message)),
                        () => OreScannerSnapshots.Disable()));
                oreDiscovery = InstallContentHook(oreDiscovery, "EnableOreDiscovery", "ORE_DISCOVERY",
                    () => InstallContentHookPatches(oreDiscovery, patches =>
                        OreDiscoveryProvider.Install(patches, message => Logger.LogWarning(message)),
                        OreDiscoveryProvider.Disable));
                terrainMaterials = InstallContentHook(terrainMaterials, "EnableTerrainMaterialVisuals", "TERRAIN_MATERIAL_VISUALS",
                    () => InstallContentHookPatches(terrainMaterials, patches =>
                        TerrainMaterialVisuals.Install(patches, message => Logger.LogWarning(message)),
                        () => TerrainMaterialVisuals.Disable()));
                if (profile != null)
                {
                    string? admissionFailure = null;
                    var admission = new CapabilityResult("worldGenerationAdmission", true, "Managed stock world entry checks.");
                    InstallContentHookPatches(admission, patches =>
                        admissionFailure = TerrainMaterialWorldAdmission.Install(patches, game, profile), TerrainMaterialWorldAdmission.Disable);
                    if (admissionFailure != null)
                    {
                        Logger.LogWarning(admissionFailure);
                        terrainMaterialRegistration = new CapabilityResult(terrainMaterialRegistration.Name, false, admissionFailure);
                        celestialLayout = new CapabilityResult(celestialLayout.Name, false, admissionFailure);
                        bodyEnvironment = new CapabilityResult(bodyEnvironment.Name, false, admissionFailure);
                        terrainPlacement = new CapabilityResult(terrainPlacement.Name, false, admissionFailure);
                        planetTerrainGeneration = new CapabilityResult(planetTerrainGeneration.Name, false, admissionFailure);
                        asteroidTerrainGeneration = new CapabilityResult(asteroidTerrainGeneration.Name, false, admissionFailure);
                        if (TerrainMaterialRegistration.HasPersistentTerrainMaterialState(profile))
                            Fail("Managed profile needs an unavailable terrain material world-entry guard", new InvalidOperationException(admissionFailure));
                    }
                }
                bodyEnvironment = InstallContentHook(bodyEnvironment, "EnableBodyEnvironment", "BODY_ENVIRONMENT",
                    () => InstallContentHookPatches(bodyEnvironment, patches =>
                        BodyEnvironmentProvider.Install(patches, message => Logger.LogWarning(message)),
                        BodyEnvironmentProvider.Disable));
                terrainTextureOverrides = InstallContentHook(terrainTextureOverrides, "EnableTerrainTextureOverrides", "TERRAIN_TEXTURE_OVERRIDES",
                    () => InstallContentHookPatches(terrainTextureOverrides, patches =>
                        TerrainTextureOverride.Install(patches, message => Logger.LogWarning(message)),
                        TerrainTextureOverride.Disable));
                terrainMaterialRegistration = InstallContentHook(terrainMaterialRegistration,
                    "EnableTerrainMaterialRegistration", "TERRAIN_MATERIAL_REGISTRATION",
                    () => InstallContentHookPatches(terrainMaterialRegistration,
                        patches => TerrainMaterialRegistration.Install(patches, profile!, message => Logger.LogWarning(message)),
                        TerrainMaterialRegistration.Disable));
                if (terrainMiningDrops.Available && (!items.Available || !terrainMaterialRegistration.Available))
                    terrainMiningDrops = new CapabilityResult(terrainMiningDrops.Name, false,
                        "Requires item and terrain material registration.");
                terrainMiningDrops = InstallContentHook(terrainMiningDrops, "EnableTerrainMiningDrops", "TERRAIN_MINING_DROPS",
                    () => InstallContentHookPatches(terrainMiningDrops, patches =>
                        TerrainMiningDropRegistration.Install(patches, message => Logger.LogWarning(message)),
                        TerrainMiningDropRegistration.Disable));
                celestialLayout = InstallContentHook(celestialLayout, "EnableCelestialLayout", "CELESTIAL_LAYOUT",
                    () => InstallContentHookPatches(celestialLayout, patches =>
                        CelestialLayoutProvider.Install(patches, message => Logger.LogWarning(message), celestialLayoutCorrectPoiOrdinal),
                        CelestialLayoutProvider.Disable));
                if (galaxyCatalogPreview.Available) GalaxyCatalogPreview.Enable();
                bool galaxyRequested = Config.Bind("Experimental", "EnableGalaxyCatalogPublication", false,
                    "Allow a declared galaxy catalog in a marked managed solo save root.").Value
                    || Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_GALAXY_CATALOG") == "1";
                galaxyCatalogPublication = GalaxyCatalogProvider.Install(profile, galaxyCatalogPublication,
                    galaxyRequested, message => Logger.LogWarning(message));
                contentHookStops.Add(GalaxyCatalogProvider.Disable);
                Logger.LogInfo(galaxyCatalogPublication.Name + ": " + galaxyCatalogPublication.Reason);
                terrainPlacement = InstallContentHook(terrainPlacement, "EnableTerrainPlacement", "TERRAIN_PLACEMENT",
                    () => InstallContentHookPatches(terrainPlacement,
                        patches => TerrainPlacement.Install(patches, message => Logger.LogWarning(message)),
                        TerrainPlacement.Disable));
                if (terrainRadialPlacement.Available && !terrainPlacement.Available)
                    terrainRadialPlacement = new CapabilityResult(terrainRadialPlacement.Name, false, "Requires the terrain placement hook.");
                terrainRadialPlacement = InstallContentHook(terrainRadialPlacement, "EnableTerrainRadialPlacement", "TERRAIN_RADIAL_PLACEMENT", () =>
                {
                    TerrainPlacement.EnableRadialBands();
                    return terrainRadialPlacement;
                });
                if (terrainAuthoredSurfacePlacement.Available && !terrainPlacement.Available)
                    terrainAuthoredSurfacePlacement = new CapabilityResult(terrainAuthoredSurfacePlacement.Name, false, "Requires the terrain placement hook.");
                terrainAuthoredSurfacePlacement = InstallContentHook(terrainAuthoredSurfacePlacement,
                    "EnableTerrainAuthoredSurfacePlacement", "TERRAIN_AUTHORED_SURFACE_PLACEMENT", () =>
                    {
                        TerrainPlacement.EnableAuthoredSurfaceBands();
                        return terrainAuthoredSurfacePlacement;
                    });
                planetTerrainGeneration = InstallContentHook(planetTerrainGeneration, "EnablePlanetTerrainGeneration", "PLANET_TERRAIN_GENERATION",
                    () => InstallContentHookPatches(planetTerrainGeneration, patches =>
                        TerrainGenerationProvider.InstallPlanet(patches, message => Logger.LogWarning(message)),
                        TerrainGenerationProvider.DisablePlanet));
                asteroidTerrainGeneration = InstallContentHook(asteroidTerrainGeneration, "EnableAsteroidTerrainGeneration", "ASTEROID_TERRAIN_GENERATION",
                    () => InstallContentHookPatches(asteroidTerrainGeneration, patches =>
                        TerrainGenerationProvider.InstallAsteroid(patches, message => Logger.LogWarning(message)),
                        TerrainGenerationProvider.DisableAsteroid));
                if (!experimental && storageConnectors.Available)
                    storageConnectors = new CapabilityResult(storageConnectors.Name, false, "Requires managed experimental brick registration.");
                storageConnectors = InstallContentHook(storageConnectors, "EnableStorageConnectors", "STORAGE_CONNECTORS",
                    () => InstallContentHookPatches(storageConnectors, StorageConnectorRegistration.Install,
                        StorageConnectorRegistration.Disable));
                if (!storageConnectors.Available && flushStoragePlacement.Available)
                    flushStoragePlacement = new CapabilityResult(flushStoragePlacement.Name, false, "Requires native storage connectors.");
                flushStoragePlacement = InstallContentHook(flushStoragePlacement, "EnableFlushStoragePlacement", "FLUSH_STORAGE_PLACEMENT",
                    () => InstallContentHookPatches(flushStoragePlacement, FlushStoragePlacement.Install, FlushStoragePlacement.Disable));
                if (!experimental && nativeBlockVariants.Available)
                    nativeBlockVariants = new CapabilityResult(nativeBlockVariants.Name, false, "Requires managed experimental brick registration.");
                nativeBlockVariants = InstallContentHook(nativeBlockVariants, "EnableNativeBlockVariants", "NATIVE_BLOCK_VARIANTS",
                    () =>
                    {
                        NativeBlockVariantRegistration.Enable();
                        return nativeBlockVariants;
                    }, NativeBlockVariantRegistration.Disable);
                contextActions = InstallContentHook(contextActions, "EnableContextActions", "CONTEXT_ACTIONS",
                    () => InstallContentHookPatches(contextActions,
                        patches => ContextActionProvider.Install(patches, message => Logger.LogWarning(message)),
                        ContextActionProvider.Disable));
                if (!contextActions.Available && inventoryViews.Available)
                    inventoryViews = new CapabilityResult(inventoryViews.Name, false, "Requires the context action hook.");
                inventoryViews = InstallContentHook(inventoryViews, "EnableInventoryViews", "INVENTORY_VIEWS",
                    () =>
                    {
                        InventoryViewService.Enable();
                        return inventoryViews;
                    }, InventoryViewService.Disable);
                worldSurfaceQuery = InstallContentHook(worldSurfaceQuery, "EnableWorldSurfaceQuery", "WORLD_SURFACE_QUERY",
                    () =>
                    {
                        WorldSurfaceQuery.Enable();
                        return worldSurfaceQuery;
                    }, WorldSurfaceQuery.Disable);
                if (terrainEdits.Available && !worldSurfaceQuery.Available)
                    terrainEdits = new CapabilityResult(terrainEdits.Name, false, "Requires the world surface query hook.");
                terrainEdits = InstallContentHook(terrainEdits, "EnableTerrainEdits", "TERRAIN_EDITS",
                    () =>
                    {
                        TerrainEditService.Enable(message => Logger.LogWarning(message));
                        return terrainEdits;
                    }, TerrainEditService.Disable);
                seatedPoseTargets = InstallContentHook(seatedPoseTargets, "EnableSeatedPoseTargets", "SEATED_POSE_TARGETS",
                    () => InstallContentHookPatches(seatedPoseTargets,
                        patches => SeatedPoseTargetProvider.Install(patches, message => Logger.LogWarning(message)),
                        SeatedPoseTargetProvider.Disable));
                dismountPoses = InstallContentHook(dismountPoses, "EnableDismountPoses", "DISMOUNT_POSES",
                    () => InstallContentHookPatches(dismountPoses,
                        patches => DismountPoseProvider.Install(patches, message => Logger.LogWarning(message)),
                        DismountPoseProvider.Disable));
                blockAssemblies = InstallContentHook(blockAssemblies, "EnableBlockAssemblies", "BLOCK_ASSEMBLIES",
                    () =>
                    {
                        BlockAssemblyProvider.Install();
                        return blockAssemblies;
                    }, BlockAssemblyProvider.Disable);
                if (shipyardAssemblyEligibility.Available && !blockAssemblies.Available)
                    shipyardAssemblyEligibility = new CapabilityResult(shipyardAssemblyEligibility.Name, false,
                        "Requires the block assembly hook.");
                shipyardAssemblyEligibility = InstallContentHook(shipyardAssemblyEligibility,
                    "EnableShipyardAssemblyEligibility", "SHIPYARD_ASSEMBLY_ELIGIBILITY",
                    () => InstallContentHookPatches(shipyardAssemblyEligibility,
                        patches => ShipyardAssemblyEligibility.Install(patches, message => Logger.LogWarning(message)),
                        ShipyardAssemblyEligibility.Disable));
                var machineWorkRateModifiers = new CapabilityResult("machineWorkRateModifiers",
                    machineRecipes.Available && blockAssemblies.Available, machineRecipes.Available && blockAssemblies.Available
                        ? "Audited powered recipe progress and live assembly bindings are available."
                        : "Requires machine recipes and block assemblies.");
                machineWorkRateModifiers = InstallContentHook(machineWorkRateModifiers,
                    "EnableMachineWorkRateModifiers", "MACHINE_WORK_RATE_MODIFIERS",
                    () =>
                    {
                        MachineWorkRateModifier.Install();
                        return machineWorkRateModifiers;
                    }, MachineWorkRateModifier.Disable);
                transferLinks = InstallContentHook(transferLinks, "EnableTransferLinks", "TRANSFER_LINKS",
                    () => InstallContentHookPatches(transferLinks, TransferLinkService.Install, TransferLinkService.Disable));
                gridPhysics = InstallContentHook(gridPhysics, "EnableGridPhysics", "GRID_PHYSICS",
                    () => InstallContentHookPatches(gridPhysics,
                        patches => GridPhysicsStep.Install(patches, message => Logger.LogWarning(message)),
                        GridPhysicsStep.Disable));
                if (sublightControl.Available && !gridPhysics.Available)
                    sublightControl = new CapabilityResult(sublightControl.Name, false, "Requires the grid physics hook.");
                sublightControl = InstallContentHook(sublightControl, "EnableSublightControl", "SUBLIGHT_CONTROL", () =>
                {
                    SublightControlService.Enable(message => Logger.LogWarning(message));
                    return sublightControl;
                }, SublightControlService.Disable);
                cameraView = InstallContentHook(cameraView, "EnableCameraView", "CAMERA_VIEW",
                    () => InstallContentHookPatches(cameraView, CameraViewService.Install, CameraViewService.Disable));
                mechanismPaths = InstallContentHook(mechanismPaths, "EnableMechanismPaths", "MECHANISM_PATHS",
                    () => InstallContentHookPatches(mechanismPaths,
                        patches => MechanismPathProvider.Install(patches, message => Logger.LogWarning(message)),
                        MechanismPathProvider.Disable));
                gridAttachments = InstallContentHook(gridAttachments, "EnableGridAttachments", "GRID_ATTACHMENTS", () =>
                {
                    GridAttachmentService.Enable(message => Logger.LogWarning(message));
                    return gridAttachments;
                }, GridAttachmentService.Disable);
                gridDestruction = InstallContentHook(gridDestruction, "EnableGridDestruction", "GRID_DESTRUCTION",
                    () => InstallContentHookPatches(gridDestruction,
                        patches => GridDestruction.Install(patches, message => Logger.LogWarning(message)),
                        GridDestruction.Disable));
                navigationMarkers = InstallContentHook(navigationMarkers, "EnableNavigationMarkers", "NAVIGATION_MARKERS",
                    () => InstallContentHookPatches(navigationMarkers,
                        patches => NavigationMarkerProvider.Install(patches, message => Logger.LogWarning(message)),
                        NavigationMarkerProvider.Disable));
                navigationTargets = InstallContentHook(navigationTargets, "EnableNavigationTargets", "NAVIGATION_TARGETS",
                    () =>
                    {
                        NavigationTargetService.Install();
                        return navigationTargets;
                    }, NavigationTargetService.Disable);
                if (logicOutputs.Available && !KeelFunctionalCommand.Enabled)
                    logicOutputs = new CapabilityResult(logicOutputs.Name, false, "Requires functional command registration.");
                logicOutputs = InstallContentHook(logicOutputs, "EnableLogicOutputs", "LOGIC_OUTPUTS",
                    () =>
                    {
                        LogicOutputProvider.Enable();
                        return logicOutputs;
                    }, LogicOutputProvider.Disable);
                vehicleControl = InstallContentHook(vehicleControl, "EnableVehicleControlProvider", "VEHICLE_CONTROL_PROVIDER",
                    () => InstallContentHookPatches(vehicleControl, VehicleControlProvider.Install, VehicleControlProvider.Disable));
                waypointFlight = InstallContentHook(waypointFlight, "EnableWaypointFlight", "WAYPOINT_FLIGHT",
                    () => InstallContentHookPatches(waypointFlight, WaypointFlightService.Install, WaypointFlightService.Disable));
                gridRecovery = InstallContentHook(gridRecovery, "EnableGridRecovery", "GRID_RECOVERY",
                    () =>
                    {
                        GridRecoveryService.Enable();
                        return gridRecovery;
                    }, GridRecoveryService.Disable);
                if (!experimental && (cameraView.Available || vehicleControl.Available || waypointFlight.Available || logicOutputs.Available || navigationMarkers.Available || navigationTargets.Available || gridAttachments.Available || gridDestruction.Available || mechanismPaths.Available || gridPhysics.Available || transferLinks.Available || blockAssemblies.Available || shipyardAssemblyEligibility.Available || terrainEdits.Available || seatedPoseTargets.Available || dismountPoses.Available || gridRecovery.Available || worldSurfaceQuery.Available || contextActions.Available || inventoryViews.Available || items.Available || machineRecipes.Available || gathering.Available || recipes.Available || fluids.Available
                    || oreGeneration.Available || oreScanner.Available || oreDiscovery.Available || terrainMaterials.Available
                    || bodyEnvironment.Available || terrainTextureOverrides.Available || terrainMaterialRegistration.Available || celestialLayout.Available || galaxyCatalogPublication.Available || terrainPlacement.Available || planetTerrainGeneration.Available || asteroidTerrainGeneration.Available))
                    status.Mode = "experimental-content-hooks";
                Logger.LogInfo("Assembly diagnostic MVID="+status.AssemblyMvid+"; no identity admission gate. Mode="+status.Mode);
                status.Capabilities=capabilities.Values.Concat(new[]{functionalCapability,gateCapability,attachmentCapability,
                    visualCapability,materialCapability,paintCapability,repeatCapability,defaultPaintCapability,modularCapability,
                    codexCapability, items, itemVisuals, tintedItemSurface, machineRecipes, gathering, recipes, fluids, oreGeneration, oreScanner, oreDiscovery, terrainMaterials,
                    bodyEnvironment, terrainTextureOverrides, terrainMaterialRegistration, celestialLayout, galaxyCatalogPreview, galaxyCatalogPublication, terrainPlacement, terrainRadialPlacement, terrainAuthoredSurfacePlacement, terrainMiningDrops, planetTerrainGeneration, asteroidTerrainGeneration, storageConnectors, flushStoragePlacement, nativeBlockVariants, contextActions, inventoryViews, worldSurfaceQuery, gridRecovery, terrainEdits, seatedPoseTargets, dismountPoses, blockAssemblies, shipyardAssemblyEligibility, machineWorkRateModifiers, transferLinks, gridPhysics, sublightControl, cameraView, mechanismPaths, gridAttachments, gridDestruction, navigationMarkers, navigationTargets, logicOutputs, vehicleControl, waypointFlight})
                    .Select(result=>new StatusCapability{Name=result.Name,Available=result.Available,Reason=result.Reason}).ToArray();
                Logger.LogInfo(functionalCapability.Name+": "+functionalCapability.Reason);
                Logger.LogInfo(gateCapability.Name+": "+gateCapability.Reason);
                Logger.LogInfo(attachmentCapability.Name+": "+attachmentCapability.Reason);
                Logger.LogInfo(visualCapability.Name+": "+visualCapability.Reason);
                Logger.LogInfo(materialCapability.Name+": "+materialCapability.Reason);
                Logger.LogInfo(paintCapability.Name+": "+paintCapability.Reason);
                Logger.LogInfo(repeatCapability.Name+": "+repeatCapability.Reason);
                Logger.LogInfo(defaultPaintCapability.Name+": "+defaultPaintCapability.Reason);
                Logger.LogInfo(modularCapability.Name+": "+modularCapability.Reason);
                Logger.LogInfo(codexCapability.Name+": "+codexCapability.Reason);
                if(!Available("lifecycle"))
                {
                    status.Reason="Audit metadata available; lifecycle observation unavailable.";
                    Publish(); return;
                }
                status.Reason="Awaiting native resource cycle; resource readiness does not authorize world/blueprint loading.";
                Publish();
            }
            catch(Exception exception)
            {
                StopContentHooks();
                KeelFunctionalCommand.Enabled=false;
                FunctionalEventGate.Enabled=false;
                WeaponAttachmentAvailability.Enabled=false;
                TryUnpatch(gateHarmony);
                TryUnpatch(functionalHarmony);
                TryUnpatch(harmony);
                Fail("Framework initialization failed",exception);
            }
        }
        private void InstallBrickLifecycle(Assembly game)
        {
            if (!Available("lifecycle")) return;
            try
            {
                adapter=new GameAdapter();
                harmony=new Harmony(Id);
                if(experimental) StockTemplateResources.Install(harmony,game);
                Patch(game,"VoxelEngine.Brick","LoadAllResourcesRuntime",nameof(BeforeBricks),nameof(AfterBricks),nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","OnGameReload",nameof(BeforeReload),null,nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","OnResourceLoaded",nameof(BeforeReady),nameof(AfterReady),nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","ReleaseResources",nameof(BeforeRelease),null,null);
            }
            catch (Exception exception)
            {
                TryUnpatch(harmony);
                experimental = false;
                status.Mode = "audit";
                status.ContractsValidated = false;
                var reason = "Brick lifecycle hooks unavailable: " + exception.Message;
                capabilities = capabilities.ToDictionary(pair => pair.Key, pair => pair.Key == "lifecycle"
                    ? new CapabilityResult("lifecycle", false, reason) : pair.Value);
                Logger.LogWarning(reason);
            }
        }

        private CapabilityResult InstallContentHook(CapabilityResult capability, string setting, string flag,
            Func<CapabilityResult> install, Action? stop = null)
        {
            var requested = Config.Bind("Experimental", setting, false,
                "Enable this content hook in a managed disposable solo profile.").Value
                || Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_" + flag) == "1";
            var result = capability;
            if (capability.Available)
            {
                if (!requested || profile == null)
                    result = new CapabilityResult(capability.Name, false,
                        requested ? "Requires a valid managed profile." : "Compatible and disabled by default.");
                else
                {
                    try
                    {
                        result = install();
                        if (result.Available && stop != null) contentHookStops.Add(stop);
                    }
                    catch (Exception exception)
                    {
                        result = new CapabilityResult(capability.Name, false, "Hook installation failed. " + exception.Message);
                    }
                }
            }
            Logger.LogInfo(result.Name + ": " + result.Reason);
            return result;
        }

        private CapabilityResult InstallContentHookPatches(CapabilityResult capability, Action<Harmony> install, Action disable)
        {
            var patches = new Harmony(Id + "." + capability.Name);
            try
            {
                install(patches);
                contentHookStops.Add(() =>
                {
                    try { disable(); }
                    finally { patches.UnpatchSelf(); }
                });
                return capability;
            }
            catch
            {
                TryUnpatch(patches);
                throw;
            }
        }

        private void StopContentHooks()
        {
            foreach (var stop in contentHookStops)
            {
                try { stop(); }
                catch (Exception exception) { Logger.LogError("Cannot stop a content hook: " + exception); }
            }
            contentHookStops.Clear();
        }

        private void TryUnpatch(Harmony? patches)
        {
            try { patches?.UnpatchSelf(); }
            catch (Exception exception) { Logger.LogError("Cannot remove Harmony patches for " + patches?.Id + ": " + exception); }
        }

        private CapabilityResult InstallEventGate(CapabilityResult capability)
        {
            try
            {
                gateHarmony=new Harmony(Id+".eventgate");
                FunctionalEventGate.Install(gateHarmony);
                FunctionalEventGate.Enabled=true;
                return capability;
            }
            catch(Exception exception)
            {
                TryUnpatch(gateHarmony);
                return new CapabilityResult(capability.Name,false,"Event Gate hook unavailable: "+exception.Message);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private CapabilityResult InstallWeaponAttachmentHooks(Assembly game,CapabilityResult capability,bool interaction)
        {
            try
            {
                var hooks=new Harmony(Id+(interaction ? ".weaponattachments.interaction" : ".weaponattachments"));
                if(interaction)
                {
                    ManagedWeaponAttachmentBrick.InstallInteractionHooks(hooks,game);
                    attachmentInteractionHarmony=hooks;
                    WeaponAttachmentAvailability.Enabled=true;
                }
                else
                {
                    ManagedWeaponAttachmentBrick.InstallCoreHooks(hooks,game);
                    attachmentHarmony=hooks;
                }
                return capability;
            }
            catch(Exception exception)
            {
                Logger.LogWarning(exception.ToString());
                return new CapabilityResult(capability.Name,false,"Weapon attachment hooks unavailable: "+exception.Message);
            }
        }
        private CapabilityResult InstallFunctionalCommands(Assembly game,CapabilityResult capability)
        {
            try
            {
                functionalHarmony=new Harmony(Id+".functional");
                KeelFunctionalCommand.InstallSwitchHook(functionalHarmony,game);
                KeelFunctionalCommand.Enabled=true;
                return capability;
            }
            catch(Exception exception)
            {
                TryUnpatch(functionalHarmony);
                return new CapabilityResult(capability.Name,false,"Stock switch hook unavailable: "+exception.Message);
            }
        }
        internal static void ReportFunctionalFailure(string key,Exception exception) =>
            instance?.Logger.LogError("Functional placement stopped for "+key+". "+exception);

        internal static bool ReceiverRepeatsAvailable(out string reason)
        {
            var report=instance?.status.Capabilities.Single(value=>value.Name=="receiverRepeats");
            reason=report?.Reason ?? "Native receiver repeat capability is unavailable.";
            return report?.Available==true;
        }

        internal static UI.Codex.CodexTab ResolveCodexCategory(string? name,UI.Codex.CodexTab fallback,string key)
        {
            if(name==null || instance is not { } plugin) return fallback;
            var report=plugin.status.Capabilities.Single(value=>value.Name=="codexCategories");
            if(report.Available && CodexCategories.Find(name,plugin.codexMenus) is { } category) return category;
            plugin.Logger.LogWarning("Using the default Codex category for "+key+": "
                +(report.Available ? name+" is unavailable in the visible native Codex." : report.Reason));
            return fallback;
        }

        internal static PreparedAttachmentVisual? PrepareAttachmentVisual(string key,VoxelEngine.Brick template,
            Func<WeaponAttachmentVisual?> selectVisual,bool requiresRepeatRendering,UnityEngine.Bounds? renderEnvelope)
        {
            var plugin=instance;
            if(plugin==null) return null;
            var report=plugin.status.Capabilities.Single(value=>value.Name=="attachmentVisuals");
            try
            {
                if(!report.Available) throw new InvalidOperationException(report.Reason);
                var visual=selectVisual();
                if(visual==null) return null;
                if(requiresRepeatRendering) plugin.EnsureReceiverRepeatHook();
                plugin.EnsureAttachmentVisualHook(report);
                var paintReport=plugin.status.Capabilities.Single(value=>value.Name=="attachmentDefaultPaint");
                if(!paintReport.Available && (visual.DefaultPaint!=null || visual.PreviewPaint!=null))
                    plugin.Logger.LogWarning("Using stock paint defaults for "+key+": "+paintReport.Reason);
                var prepared=new PreparedAttachmentVisual(
                    AttachmentVisualResources.Prepare(visual,template,key,renderEnvelope:renderEnvelope),
                    paintReport.Available ? visual : null,renderEnvelope);
                if(visual.Materials!=null)
                {
                    var materialReport=plugin.status.Capabilities.Single(value=>value.Name=="attachmentMaterials");
                    if(materialReport.Available) plugin.attachmentMaterials.Stage(key,template,visual,prepared);
                    else plugin.Logger.LogWarning("Using baked material fallbacks for "+key+": "+materialReport.Reason);
                }
                return prepared;
            }
            catch(Exception exception)
            {
                ReportVisualFallback(key,exception.Message);
                return null;
            }
        }

        internal static void ReportVisualFallback(string key,string reason) =>
            instance?.Logger.LogWarning("Using stock visual for "+key+": "+reason);

        internal static bool TryEnableModularReceiverRendering(out string reason)
        {
            var plugin=instance;
            if(plugin==null)
            {
                reason="The modular receiver session is unavailable.";
                return false;
            }
            var report=plugin.status.Capabilities.Single(value=>value.Name=="modularReceiverRendering");
            reason=report.Reason;
            if(!report.Available) return false;
            if(plugin.modularReceiverHarmony!=null) return true;
            var hook=new Harmony(Id+".modularreceivers");
            try
            {
                ReceiverModularRendering.Install(hook);
                plugin.modularReceiverHarmony=hook;
                return true;
            }
            catch(Exception exception)
            {
                report.Available=false;
                reason=report.Reason="Native modular receiver hooks unavailable: "+exception.Message;
                try { hook.UnpatchSelf(); }
                catch(Exception cleanup) { plugin.Logger.LogWarning("Modular receiver hook cleanup failed: "+cleanup.Message); }
                return false;
            }
        }

        private void EnsureReceiverRepeatHook()
        {
            if(receiverRepeatHarmony!=null) return;
            var report=status.Capabilities.Single(value=>value.Name=="receiverRepeats");
            if(!report.Available) throw new InvalidOperationException(report.Reason);
            var hook=new Harmony(Id+".receiverrepeats");
            try
            {
                ReceiverRepeatRendering.Install(hook);
                receiverRepeatHarmony=hook;
            }
            catch(Exception exception)
            {
                report.Available=false;
                report.Reason="Native receiver repeat rendering hook unavailable: "+exception.Message;
                try { hook.UnpatchSelf(); }
                catch(Exception cleanup) { Logger.LogWarning("Receiver repeat hook cleanup failed: "+cleanup.Message); }
                throw new InvalidOperationException(report.Reason,exception);
            }
        }

        private void EnsureAttachmentVisualHook(StatusCapability report)
        {
            if(attachmentVisualHarmony!=null) return;
            var hook=new Harmony(Id+".attachmentvisuals");
            try
            {
                AttachmentVisualResources.Install(hook);
                attachmentVisualHarmony=hook;
            }
            catch(Exception exception)
            {
                report.Available=false;
                report.Reason="Native attachment visual hook unavailable: "+exception.Message;
                try { hook.UnpatchSelf(); }
                catch(Exception cleanup) { Logger.LogWarning("Attachment visual hook cleanup failed: "+cleanup.Message); }
                throw new InvalidOperationException(report.Reason,exception);
            }
        }
        private bool Available(string name)=>capabilities.TryGetValue(name,out var result) && result.Available;
        private void Patch(Assembly game,string typeName,string methodName,string? prefix,string? postfix,string? finalizer)
        {
            var type=game.GetType(typeName,false) ?? throw new InvalidOperationException("Missing patch type: "+typeName);
            var method=AccessTools.Method(type,methodName,Type.EmptyTypes) ?? throw new InvalidOperationException("Missing patch method: "+typeName+"."+methodName);
            HarmonyMethod? Hook(string? name)=>name==null ? null : new HarmonyMethod(typeof(Plugin).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
            harmony!.Patch(method,Hook(prefix),Hook(postfix),null,Hook(finalizer),null);
        }
        private void Publish()
        {
            if(profile==null) return;
            try { profile.WriteStatus(status); }
            catch(Exception exception)
            {
                Logger.LogError("Cannot publish managed runtime status: "+exception.Message);
                if(experimental) {status.Failed=true;status.ResourcesReady=false;throw;}
            }
        }
        private void Fail(string reason,Exception exception)
        {
            StopFunctionalCommands();
            KeelFunctionalCommand.Enabled=false;
            FunctionalEventGate.Enabled=false;
            WeaponAttachmentAvailability.Enabled=false;
            awaitingAttachmentInteraction=false;
            pendingAttachmentGame=null;
            pendingAttachmentCapability=null;
            status.Failed=true; status.RegistrationAdmitted=false; status.ResourcesReady=false; resourceChecksPassed=false;
            status.Reason=reason+": "+exception.Message
                +(exception.InnerException==null ? "" : " Root cause: "+exception.GetBaseException().Message)
                +". Restart required; no native rollback attempted.";
            Logger.LogError(status.Reason+"\n"+exception);
            try { Publish(); } catch(Exception publishError) {Logger.LogError("Failed status could not be persisted: "+publishError.Message);}
        }
        private void EnsureNotFailed()
        {
            if(experimental && status.Failed) throw new InvalidOperationException("Managed content session failed; restart required.");
        }
        private static void BeforeBricks()=>instance?.OnBeforeBricks();
        private void OnBeforeBricks()
        {
            EnsureNotFailed(); status.ResourcesReady=false; resourceChecksPassed=false;
            if(experimental && registration?.Owned.Count>0)
                throw new InvalidOperationException("Native stock initialization repeated with managed bricks present.");
        }
        private static void AfterBricks()
        {
            var plugin=instance; if(plugin==null) return;
            try
            {
                plugin.AdmitDefinitions();
                if(plugin.Available("brickRegistry")) plugin.adapter!.ObserveBricks(plugin.status);
                plugin.Publish();
            }
            catch(Exception exception) {plugin.Fail("Brick initialization validation failed",exception); if(plugin.experimental) throw;}
        }
        private void ReportAttachmentCapability(CapabilityResult result)
        {
            var report=status.Capabilities.Single(value=>value.Name==result.Name);
            report.Available=result.Available;
            report.Reason=result.Reason;
            Logger.LogInfo(result.Name+": "+result.Reason);
        }
        private void AdmitDefinitions()
        {
            var definitions=ContentRegistration.Catalog.Seal();
            if(!experimental)
            {
                if(definitions.Count>0) Logger.LogInfo("Audit mode: ignored "+definitions.Count+" content factories; no IDs allocated.");
                return;
            }
            if(pendingAttachmentGame!=null && pendingAttachmentCapability!=null)
            {
                if(definitions.Any(definition=>definition.RequiresWeaponAttachments))
                {
                    var result=InstallWeaponAttachmentHooks(pendingAttachmentGame,pendingAttachmentCapability,interaction:false);
                    if(!result.Available) ReportAttachmentCapability(result);
                }
                if(attachmentHarmony==null)
                {
                    pendingAttachmentGame=null;
                    pendingAttachmentCapability=null;
                }
            }
            bool hullAvailable=Available("stockHull") && definitions.Any(definition=>definition.Kind==ContentKind.Ordinary)
                && CheckPreset("stockHull",GameAdapter.IsStockHullAvailable);
            var admitted=new List<ContentDefinition>();
            foreach(var definition in definitions)
            {
                var failure=definition.PrepareTemplate is { } prepare
                    ? CheckPreparedTemplate(prepare,!definition.RequiresWeaponAttachments || attachmentHarmony!=null,
                        definition.Kind is not (ContentKind.StorageConnector or ContentKind.NativeBlockVariant))
                    : hullAvailable ? null : "The legacy stock hull contract is unavailable.";
                if(failure==null)
                {
                    if(definition.OwnedRepeat is { } repeat) admitted.Add(repeat);
                    admitted.Add(definition);
                }
                else Logger.LogWarning("Skipped "+definition.Key+": "+failure);
            }
            var spaces=adapter!.AllocationSpaces(limits!);
            var saved=profile!.ReadRegistry();
            var allocator=saved==null ? new ContentAllocator(spaces,profile.ProfileId) : ContentAllocator.FromJson(saved,spaces);
            registration=new NativeRegistrationSession(adapter);
            registration.Commit(admitted,allocator,profile.WriteRegistry);
            var paintReport=status.Capabilities.Single(value=>value.Name=="attachmentPaint");
            var materialFailure=attachmentMaterials.Commit(paintReport.Available,message=>Logger.LogWarning(message));
            if(materialFailure!=null)
            {
                var report=status.Capabilities.Single(value=>value.Name=="attachmentMaterials");
                report.Available=false;
                report.Reason=materialFailure;
                paintReport.Available=false;
                paintReport.Reason="Native attachment materials are unavailable.";
                Logger.LogWarning(materialFailure);
            }
            adapter.InitializeManagedResources(registration);
            adapter.ValidateResources(registration,limits!,false);
            status.RegistrationAdmitted=true;
            status.RegisteredDefinitions=registration.Owned.Count;
            status.RegisteredFunctionalBlocks=admitted.Count(definition=>definition.IsFunctional);
            Logger.LogInfo("Experimental definitions admitted: "+status.RegisteredDefinitions+", functional: "+status.RegisteredFunctionalBlocks);
        }
        private static string? CheckPreparedTemplate(Func<string?> prepare,bool featureAvailable,bool requiresFunctional)
        {
            if(requiresFunctional && !KeelFunctionalCommand.Enabled) return "Functional commands are unavailable or disabled.";
            if(!featureAvailable) return "Weapon attachment contracts are unavailable or disabled.";
            try { return prepare(); }
            catch(Exception exception) { return "Native template preparation failed: "+exception.Message; }
        }
        private delegate bool PresetPreflight(out string reason);
        private bool CheckPreset(string name,PresetPreflight check)
        {
            string reason;
            try
            {
                if(check(out reason)) return true;
            }
            catch(Exception exception)
            {
                reason="Live native preset validation failed: "+exception.Message;
            }
            var capability=status.Capabilities.Single(value=>value.Name==name);
            capability.Available=false;
            capability.Reason=reason;
            Logger.LogWarning(name+": "+reason);
            return false;
        }
        private void Update()
        {
            if(KeelFunctionalCommand.Enabled) KeelFunctionalCommand.StopOutsideSolo();
            // Wait until the resource loader returns to the player loop. JIT-compiling native interaction
            // methods can initialize UI and crafting types that require fully allocated game resources.
            if(awaitingAttachmentInteraction && pendingAttachmentGame!=null && pendingAttachmentCapability!=null)
            {
                awaitingAttachmentInteraction=false;
                var result=InstallWeaponAttachmentHooks(pendingAttachmentGame,pendingAttachmentCapability,interaction:true);
                pendingAttachmentGame=null;
                pendingAttachmentCapability=null;
                ReportAttachmentCapability(result);
                FinishReadiness();
            }
        }
        private void OnDestroy()
        {
            if(!ReferenceEquals(instance,this)) return;
            StopContentHooks();
            StopFunctionalCommands();
            KeelFunctionalCommand.Enabled=false;
            FunctionalEventGate.Enabled=false;
            WeaponAttachmentAvailability.Enabled=false;
            attachmentVisualHarmony?.UnpatchSelf();
            modularReceiverHarmony?.UnpatchSelf();
            receiverRepeatHarmony?.UnpatchSelf();
            attachmentInteractionHarmony?.UnpatchSelf();
            attachmentHarmony?.UnpatchSelf();
            instance=null;
        }
        private static void StopFunctionalCommands()
        {
            if(KeelFunctionalCommand.Enabled) KeelFunctionalCommand.StopAll();
        }
        private static void BeforeReload()
        {
            var plugin=instance; if(plugin==null) return;
            StopFunctionalCommands();
            plugin.EnsureNotFailed(); plugin.status.ResourcesReady=false; plugin.resourceChecksPassed=false;
            plugin.status.Reason="Native resource reload started; awaiting readiness validation.";
            if(plugin.experimental) plugin.registration?.VerifyOwnership();
            plugin.Publish();
        }
        private static void BeforeRelease()
        {
            if(instance==null) return;
            StopFunctionalCommands();
            instance.status.ResourcesReady=false; instance.resourceChecksPassed=false;
            if(!instance.status.Failed) instance.status.Reason="Native resources are being released; resource readiness cleared.";
            instance.Publish();
        }
        private static void BeforeReady()
        {
            var plugin=instance; if(plugin==null) return;
            if(plugin.materialConflict!=null) return;
            plugin.EnsureNotFailed(); plugin.status.ResourcesReady=false; plugin.resourceChecksPassed=false; plugin.status.ResourceCycle++;
            try
            {
                if(plugin.Available("brickRegistry")) plugin.adapter!.ObserveBricks(plugin.status);
                if(plugin.Available("shapeAudit")) GameAdapter.ObserveShapes(plugin.status);
                if(plugin.experimental)
                {
                    if(plugin.registration==null) throw new InvalidOperationException("Resource-ready callback preceded managed registration.");
                    if(!plugin.CheckMaterialBindings()) return;
                    plugin.adapter!.ValidateResources(plugin.registration,plugin.limits!,true);
                }
                plugin.resourceChecksPassed=!plugin.status.Failed && plugin.Available("brickRegistry") && plugin.Available("nativeResources")
                    && plugin.Available("shapeAudit") && plugin.adapter!.AuditResourceTablesReady();
                plugin.Publish();
            }
            catch(Exception exception) {plugin.Fail("Resource ownership/readiness validation failed",exception);if(plugin.experimental) throw;}
        }
        private CapabilityResult InstallMaterialWorldGuard(Assembly game,CapabilityResult capability)
        {
            var hooks=new Harmony(Id+".material-world-entry");
            try
            {
                var entryPoints=new[] {
                    (Type: "UI.UI_CharacterSlot", Method: "PressButton"),
                    (Type: "UI.UI_CharacterCreation", Method: "PlayWithNewCharacter"),
                    (Type: "UI.UI_LobbySlot", Method: "ConnectToLobby")
                };
                foreach(var entry in entryPoints)
                {
                    var type=game.GetType(entry.Type);
                    var target=type==null ? null : AccessTools.DeclaredMethod(type,entry.Method,Type.EmptyTypes);
                    if(target==null || target.IsStatic || target.ReturnType!=typeof(void))
                        throw new InvalidOperationException("Missing stock world entry: "+entry.Type+"."+entry.Method);
                    hooks.Patch(target,new HarmonyMethod(typeof(Plugin),nameof(BeforeWorldEntry)) {priority=Priority.First});
                }
                return capability;
            }
            catch(Exception exception)
            {
                TryUnpatch(hooks);
                return new CapabilityResult(capability.Name,false,
                    "Stock world entry guard unavailable. Using baked material fallbacks. "+exception.Message);
            }
        }

        private bool CheckMaterialBindings()
        {
            if(materialConflict!=null) return false;
            var conflict=attachmentMaterials.BindingConflict();
            if(conflict==null) return true;
            materialConflict="Keel detected incompatible voxel materials. Restart with a compatible mod profile. "+conflict;
            Fail("Voxel material conflict; stock resource initialization can finish, but world entry is blocked",
                new InvalidOperationException(materialConflict));
            return false;
        }

        private static bool BeforeWorldEntry()
        {
            var plugin=instance;
            if(plugin==null || plugin.CheckMaterialBindings()) return true;
            UI.UI_ConfirmationWindow.OpenWindow(plugin.materialConflict!);
            return false;
        }

        private static void AfterReady()
        {
            var plugin=instance; if(plugin==null) return;
            if(plugin.status.Failed) { plugin.Publish(); return; }
            try
            {
                if(plugin.experimental) plugin.adapter!.ValidateResources(plugin.registration!,plugin.limits!,true);
            }
            catch(Exception exception) { plugin.Fail("Resource callback changed managed ownership/resources",exception); if(plugin.experimental) throw; return; }
            if(plugin.resourceChecksPassed && plugin.pendingAttachmentGame!=null)
            {
                plugin.awaitingAttachmentInteraction=true;
                return;
            }
            plugin.FinishReadiness();
        }
        private void FinishReadiness()
        {
            status.ResourcesReady=resourceChecksPassed && !status.Failed;
            status.Reason=status.ResourcesReady
                ? "Native resource callback completed. "+(experimental ? "Experimental owned brick registrations validated."
                    : status.Mode == "experimental-content-hooks" ? "Experimental content hooks installed; no brick IDs allocated."
                    : "Audit-only observation; no content IDs allocated.")+" World/blueprint admission is not established."
                : "Native resource observation incomplete; inspect capabilities and failure reason.";
            Logger.LogInfo(status.Reason+" Bricks="+status.OccupiedBricks+"/"+status.BrickCapacity+"; shapes="+status.OccupiedShapes+"/"+status.ShapeCapacity);
            Publish();
        }
        private static Exception? NativeFinalizer(Exception? __exception)
        {
            if(__exception!=null) instance?.Fail("Native resource lifecycle threw",__exception);
            return __exception;
        }
    }
}
