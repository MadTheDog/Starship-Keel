using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    [BepInPlugin(Id,"Starship Keel","0.1.4")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id="local.sevo.modplatform";
        private static Plugin? instance;
        private readonly RuntimeSnapshot status=new RuntimeSnapshot();
        private IReadOnlyDictionary<string,CapabilityResult> capabilities=new Dictionary<string,CapabilityResult>();
        private ProfileSession? profile;
        private Harmony? harmony;
        private Harmony? functionalHarmony;
        private Harmony? gateHarmony;
        private Harmony? attachmentHarmony;
        private Harmony? attachmentInteractionHarmony;
        private Harmony? attachmentVisualHarmony;
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
                    if(attachmentCapability.Available && !FunctionalStockTemplate.DiscoverReceiverTemplates(metadata.MainModule))
                        attachmentCapability=new CapabilityResult(attachmentCapability.Name,false,"No native side-slot receiver layout was found.");
                }
                foreach(var capability in capabilities.Values)
                    Logger.LogInfo(capability.Name+": "+(capability.Available ? "available — " : "unavailable — ")+capability.Reason);
                Logger.LogInfo("Assembly diagnostic MVID="+status.AssemblyMvid+"; no identity admission gate. Mode="+status.Mode);
                if(experimental && !status.ContractsValidated) throw new InvalidOperationException("Experimental registration contracts unavailable.");
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
                status.Capabilities=capabilities.Values.Concat(new[]{functionalCapability,gateCapability,attachmentCapability,visualCapability})
                    .Select(result=>new StatusCapability{Name=result.Name,Available=result.Available,Reason=result.Reason}).ToArray();
                Logger.LogInfo(functionalCapability.Name+": "+functionalCapability.Reason);
                Logger.LogInfo(gateCapability.Name+": "+gateCapability.Reason);
                Logger.LogInfo(attachmentCapability.Name+": "+attachmentCapability.Reason);
                Logger.LogInfo(visualCapability.Name+": "+visualCapability.Reason);
                if(!Available("lifecycle"))
                {
                    status.Reason="Audit metadata available; lifecycle observation unavailable.";
                    Publish(); return;
                }
                adapter=new GameAdapter();
                harmony=new Harmony(Id);
                if(experimental) StockTemplateResources.Install(harmony,game);
                Patch(game,"VoxelEngine.Brick","LoadAllResourcesRuntime",nameof(BeforeBricks),nameof(AfterBricks),nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","OnGameReload",nameof(BeforeReload),null,nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","OnResourceLoaded",nameof(BeforeReady),nameof(AfterReady),nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","ReleaseResources",nameof(BeforeRelease),null,null);
                status.Reason="Awaiting native resource cycle; resource readiness does not authorize world/blueprint loading.";
                Publish();
            }
            catch(Exception exception)
            {
                KeelFunctionalCommand.Enabled=false;
                FunctionalEventGate.Enabled=false;
                WeaponAttachmentAvailability.Enabled=false;
                gateHarmony?.UnpatchSelf();
                functionalHarmony?.UnpatchSelf();
                harmony?.UnpatchSelf();
                Fail("Framework initialization failed",exception);
            }
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
                gateHarmony?.UnpatchSelf();
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
                functionalHarmony?.UnpatchSelf();
                return new CapabilityResult(capability.Name,false,"Stock switch hook unavailable: "+exception.Message);
            }
        }
        internal static void ReportFunctionalFailure(string key,Exception exception) =>
            instance?.Logger.LogError("Functional placement stopped for "+key+". "+exception);

        internal static UnityEngine.Mesh? PrepareAttachmentVisual(string key,VoxelEngine.Brick template,
            Func<WeaponAttachmentVisual?> selectVisual)
        {
            var plugin=instance;
            if(plugin==null) return null;
            var report=plugin.status.Capabilities.Single(value=>value.Name=="attachmentVisuals");
            try
            {
                if(!report.Available) throw new InvalidOperationException(report.Reason);
                var visual=selectVisual();
                if(visual==null) return null;
                plugin.EnsureAttachmentVisualHook(report);
                return AttachmentVisualResources.Prepare(visual,template,key);
            }
            catch(Exception exception)
            {
                plugin.Logger.LogWarning("Using stock visual for "+key+": "+exception.Message);
                return null;
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
                    ? CheckPreparedTemplate(prepare,!definition.RequiresWeaponAttachments || attachmentHarmony!=null)
                    : hullAvailable ? null : "The legacy stock hull contract is unavailable.";
                if(failure==null) admitted.Add(definition);
                else Logger.LogWarning("Skipped "+definition.Key+": "+failure);
            }
            var spaces=adapter!.AllocationSpaces(limits!);
            var saved=profile!.ReadRegistry();
            var allocator=saved==null ? new ContentAllocator(spaces,profile.ProfileId) : ContentAllocator.FromJson(saved,spaces);
            registration=new NativeRegistrationSession(adapter);
            registration.Commit(admitted,allocator,profile.WriteRegistry);
            adapter.InitializeManagedResources(registration);
            adapter.ValidateResources(registration,limits!,false);
            status.RegistrationAdmitted=true;
            status.RegisteredDefinitions=registration.Owned.Count;
            status.RegisteredFunctionalBlocks=admitted.Count(definition=>definition.IsFunctional);
            Logger.LogInfo("Experimental definitions admitted: "+status.RegisteredDefinitions+", functional: "+status.RegisteredFunctionalBlocks);
        }
        private static string? CheckPreparedTemplate(Func<string?> prepare,bool featureAvailable)
        {
            if(!KeelFunctionalCommand.Enabled) return "Functional commands are unavailable or disabled.";
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
            StopFunctionalCommands();
            KeelFunctionalCommand.Enabled=false;
            FunctionalEventGate.Enabled=false;
            WeaponAttachmentAvailability.Enabled=false;
            attachmentVisualHarmony?.UnpatchSelf();
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
            plugin.EnsureNotFailed(); plugin.status.ResourcesReady=false; plugin.resourceChecksPassed=false; plugin.status.ResourceCycle++;
            try
            {
                if(plugin.Available("brickRegistry")) plugin.adapter!.ObserveBricks(plugin.status);
                if(plugin.Available("shapeAudit")) GameAdapter.ObserveShapes(plugin.status);
                if(plugin.experimental)
                {
                    if(plugin.registration==null) throw new InvalidOperationException("Resource-ready callback preceded managed registration.");
                    plugin.adapter!.ValidateResources(plugin.registration,plugin.limits!,true);
                }
                plugin.resourceChecksPassed=!plugin.status.Failed && plugin.Available("brickRegistry") && plugin.Available("nativeResources")
                    && plugin.Available("shapeAudit") && plugin.adapter!.AuditResourceTablesReady();
                plugin.Publish();
            }
            catch(Exception exception) {plugin.Fail("Resource ownership/readiness validation failed",exception);if(plugin.experimental) throw;}
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
                ? "Native resource callback completed. "+(experimental ? "Experimental owned registrations validated." : "Audit-only observation; no content IDs allocated.")+" World/blueprint admission is not established."
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
