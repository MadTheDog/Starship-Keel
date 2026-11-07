using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    [BepInPlugin(Id,"Starship Keel","0.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id="local.sevo.modplatform";
        private static Plugin? instance;
        private readonly RuntimeSnapshot status=new RuntimeSnapshot();
        private IReadOnlyDictionary<string,CapabilityResult> capabilities=new Dictionary<string,CapabilityResult>();
        private ProfileSession? profile;
        private Harmony? harmony;
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
                using(var metadata=AssemblyDefinition.ReadAssembly(game.Location))
                {
                    status.AssemblyMvid=metadata.MainModule.Mvid.ToString("D");
                    capabilities=ContractAudit.Evaluate(metadata.MainModule);
                    if(capabilities.Values.All(result=>result.Available)) limits=ContractAudit.ReadLimits(metadata.MainModule);
                }
                status.Capabilities=capabilities.Values.Select(result=>new StatusCapability{Name=result.Name,Available=result.Available,Reason=result.Reason}).ToArray();
                status.ContractsValidated=capabilities.Values.All(result=>result.Available);
                foreach(var capability in capabilities.Values)
                    Logger.LogInfo(capability.Name+": "+(capability.Available ? "available — " : "unavailable — ")+capability.Reason);
                Logger.LogInfo("Assembly diagnostic MVID="+status.AssemblyMvid+"; no identity admission gate. Mode="+status.Mode);
                if(experimental && !status.ContractsValidated) throw new InvalidOperationException("Experimental registration contracts unavailable.");
                if(!Available("lifecycle"))
                {
                    status.Reason="Audit metadata available; lifecycle observation unavailable.";
                    Publish(); return;
                }
                adapter=new GameAdapter();
                harmony=new Harmony(Id);
                Patch(game,"VoxelEngine.Brick","LoadAllResourcesRuntime",nameof(BeforeBricks),nameof(AfterBricks),nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","OnGameReload",nameof(BeforeReload),null,nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","OnResourceLoaded",nameof(BeforeReady),nameof(AfterReady),nameof(NativeFinalizer));
                Patch(game,"Manager.GameResourcesManager","ReleaseResources",nameof(BeforeRelease),null,null);
                status.Reason="Awaiting native resource cycle; resource readiness does not authorize world/blueprint loading.";
                Publish();
            }
            catch(Exception exception)
            {
                harmony?.UnpatchSelf();
                Fail("Framework initialization failed",exception);
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
            try
            {
                var definitions=ContentRegistration.Catalog.Seal();
                if(Available("brickRegistry")) adapter!.ObserveBricks(status);
                if(experimental)
                {
                    if(registration==null)
                    {
                        var spaces=adapter!.AllocationSpaces(limits!);
                        var saved=profile!.ReadRegistry();
                        var allocator=saved==null ? new ContentAllocator(spaces,profile.ProfileId) : ContentAllocator.FromJson(saved,spaces);
                        registration=new NativeRegistrationSession(adapter);
                        registration.Commit(definitions,allocator,profile.WriteRegistry);
                    }
                    else registration.VerifyOwnership();
                    status.RegistrationAdmitted=true;
                    status.RegisteredDefinitions=registration.Owned.Count;
                    Logger.LogInfo("Experimental cooperating definitions admitted: "+status.RegisteredDefinitions);
                }
                else if(definitions.Count>0) Logger.LogInfo("Audit mode: ignored "+definitions.Count+" content factories; no IDs allocated.");
                Publish();
            }
            catch(Exception exception) { Fail("Registration phase failed",exception); if(experimental) throw; }
        }
        private static void AfterBricks()
        {
            var plugin=instance; if(plugin==null) return;
            try
            {
                if(plugin.Available("brickRegistry")) plugin.adapter!.ObserveBricks(plugin.status);
                if(plugin.experimental) plugin.adapter!.ValidateResources(plugin.registration!,plugin.limits!,false);
                plugin.Publish();
            }
            catch(Exception exception) {plugin.Fail("Brick initialization validation failed",exception); if(plugin.experimental) throw;}
        }
        private static void BeforeReload()
        {
            var plugin=instance; if(plugin==null) return;
            plugin.EnsureNotFailed(); plugin.status.ResourcesReady=false; plugin.resourceChecksPassed=false;
            plugin.status.Reason="Native resource reload started; awaiting readiness validation.";
            if(plugin.experimental) plugin.registration?.VerifyOwnership();
            plugin.Publish();
        }
        private static void BeforeRelease()
        {
            if(instance==null) return;
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
            plugin.status.ResourcesReady=plugin.resourceChecksPassed && !plugin.status.Failed;
            plugin.status.Reason=plugin.status.ResourcesReady
                ? "Native resource callback completed. "+(plugin.experimental ? "Experimental owned registrations validated." : "Audit-only observation; no content IDs allocated.")+" World/blueprint admission is not established."
                : "Native resource observation incomplete; inspect capabilities and failure reason.";
            plugin.Logger.LogInfo(plugin.status.Reason+" Bricks="+plugin.status.OccupiedBricks+"/"+plugin.status.BrickCapacity+"; shapes="+plugin.status.OccupiedShapes+"/"+plugin.status.ShapeCapacity);
            plugin.Publish();
        }
        private static Exception? NativeFinalizer(Exception? __exception)
        {
            if(__exception!=null) instance?.Fail("Native resource lifecycle threw",__exception);
            return __exception;
        }
    }
}
