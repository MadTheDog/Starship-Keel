using Sevo.Framework.Sdk;
using Mono.Cecil;
var projectRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
if(!File.Exists(Path.Combine(projectRoot,"src","Sevo.Framework.Sdk","Sevo.Framework.Sdk.csproj"))) throw new Exception("Cannot resolve the Keel source root");
Directory.SetCurrentDirectory(projectRoot);
if(args.Length>0 && args[0]=="dump") {
 var asm=AssemblyDefinition.ReadAssembly(args[1]);
 foreach(var name in new[]{"ResourcesLoader","VoxelEngine.Brick","VoxelEngine.BrickCollider","Manager.GameResourcesManager","VoxelEngine.BrickIndex"}) {
  var t=asm.MainModule.GetType(name); Console.WriteLine("TYPE "+name+" "+(t!=null)); if(t==null)continue;
  foreach(var f in t.Fields) if(f.Name.Contains("INDEX") || f.Name.Contains("MASK")) Console.WriteLine(" F "+f.FullName+" const="+f.Constant);
  foreach(var m in t.Methods) if(m.IsConstructor || m.Name=="Awake" || m.Name=="LoadAllResourcesRuntime" || m.Name=="AllocateResources" || m.Name=="OnResourceLoaded" || m.Name=="GetBrickID") { Console.WriteLine(" M "+m.FullName); if(m.HasBody && (m.Name=="Awake" || m.Name=="GetBrickID")) foreach(var i in m.Body.Instructions) if(m.Name=="GetBrickID" || i.OpCode.Code==Mono.Cecil.Cil.Code.Call || i.OpCode.Code==Mono.Cecil.Cil.Code.Callvirt) Console.WriteLine("  "+i); }
 } return;
}
if(args.Length>0 && args[0]=="audit") {
 using var current=AssemblyDefinition.ReadAssembly(args.Length>1 ? args[1] : ".refs/game/Assembly-CSharp.dll");
 foreach(var capability in Sevo.Framework.Runtime.ContractAudit.Evaluate(current.MainModule).Values)
  Console.WriteLine($"{capability.Name}: {capability.Available}; {capability.Reason}");
 foreach (var capability in new[] {
  Sevo.Framework.Runtime.ContractAudit.EvaluateItems(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateContextualItems(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateItemCodex(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateTerrainDepositScanner(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateTerrainPlacement(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateAsteroidDepositCoordinates(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateStockWreckSources(current.MainModule),
  Sevo.Framework.Runtime.ContractAudit.EvaluateTerrainRadialPlacement(current.MainModule)
 }) Console.WriteLine($"{capability.Name}: {capability.Available}; {capability.Reason}");
 var functional=Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(current.MainModule);
 Console.WriteLine($"{functional.Name}: {functional.Available}; {functional.Reason}");
 var eventGate=Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalEventGate(current.MainModule);
 Console.WriteLine($"{eventGate.Name}: {eventGate.Available}; {eventGate.Reason}");
 var attachments=Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(current.MainModule);
 Console.WriteLine($"{attachments.Name}: {attachments.Available}; {attachments.Reason}");
 var limits=Sevo.Framework.Runtime.ContractAudit.ReadLimits(current.MainModule);
 Console.WriteLine($"Safe brick bound={limits.SafeBrickCapacity}; packed={limits.PackedCapacity}; properties={limits.BrickPropertiesCapacity}; collider primary={limits.ColliderPrimaryCapacity}; collider data={limits.ColliderDataCapacity}; shape templates={limits.ShapeCapacity}");
 Console.WriteLine("Diagnostic MVID="+current.MainModule.Mvid+"; no identity gate");
 return;
}
int checks=0;
using (var stationContracts = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll"))
{
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateStationBuyOffers(stationContracts).Available)
        throw new Exception("Station buy offers must admit the stock sell path.");
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateStationStock(stationContracts).Available)
        throw new Exception("Station stock must admit the stock list and kiosk path.");
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateOrbitalPoints(stationContracts).Available)
        throw new Exception("Orbital points must admit the stock catalog and spawn path.");
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateModFactions(stationContracts).Available)
        throw new Exception("Mod factions must admit the native faction loading path.");
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateStationMissions(stationContracts).Available)
        throw new Exception("Station missions must admit board and saved mission paths.");
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateSurfaceSites(stationContracts).Available
        || !Sevo.Framework.Runtime.ContractAudit.EvaluateGeneratedPoiLoot(stationContracts).Available)
        throw new Exception("Generated sites must admit native blueprint and storage operations.");
    var parent = stationContracts.GetType("VoxelEngine.ParentEntity");
    parent.Fields.Single(field => field.Name == "isNewInstance").Name = "ChangedFreshFlag";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateGeneratedPoiLoot(stationContracts).Available)
        throw new Exception("Generated loot must reject a changed fresh-instance flag.");
    if (!Sevo.Framework.Runtime.ContractAudit.EvaluateStationServices(stationContracts).Available
        || !Sevo.Framework.Runtime.ContractAudit.EvaluateStationTraffic(stationContracts).Available)
        throw new Exception("Station extensions must admit the stock action row and patrol path.");
    var buttons = stationContracts.GetType("UI.GenericActionButton");
    buttons.Fields.Single(field => field.Name == "buttonData3").Name = "ChangedServiceSlot";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateStationServices(stationContracts).Available)
        throw new Exception("Station services must reject a changed action slot.");
    var patrol = stationContracts.GetType("Game.NpcSpacePolice");
    patrol.Methods.Single(method => method.Name == "OnNewStationGenerated").Name = "ChangedPatrolSpawner";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateStationTraffic(stationContracts).Available)
        throw new Exception("Station traffic must reject a changed patrol spawner.");
    var missions = stationContracts.GetType("Game.MissionSaveData");
    missions.Fields.Single(field => field.Name == "TagByType").Name = "ChangedMissionTags";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateStationMissions(stationContracts).Available)
        throw new Exception("Station missions must reject changed saved tags.");
    var factions = stationContracts.GetType("Game.FactionTerritoryMap");
    factions.Fields.Single(field => field.Name == "factionsFromMods").Name = "ChangedFactionDefinitions";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateModFactions(stationContracts).Available)
        throw new Exception("Mod factions must reject a changed mod faction list.");
    var points = stationContracts.GetType("World.BrickEntityPOIData");
    points.Fields.Single(field => field.Name == "systemPosition").Name = "ChangedPosition";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateOrbitalPoints(stationContracts).Available)
        throw new Exception("Orbital points must reject changed native coordinates.");
    var stationStock = stationContracts.GetType("VoxelEngine.EntityStation");
    stationStock.Fields.Single(field => field.Name == "itemToSellPrices").Name = "ChangedPriceList";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateStationStock(stationContracts).Available)
        throw new Exception("Station stock must reject changed storage for prices.");
    var page = stationContracts.GetType("Game.UI_StationLandingPage");
    var sellFilter = page.Methods.Single(method => method.Name == "TryAddTradeableItem");
    sellFilter.Name = "ChangedSellFilter";
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateStationBuyOffers(stationContracts).Available)
        throw new Exception("Station buy offers must reject a changed sell filter.");
    sellFilter.Name = "TryAddTradeableItem";
    var trade = page.Methods.Single(method => method.Name == "OnTradeMade");
    var resale = trade.Body.Instructions.Single(instruction =>
        instruction.Operand is MethodReference target && target.Name == "GetItemPrice");
    resale.Previous.Previous.OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4_0;
    if (Sevo.Framework.Runtime.ContractAudit.EvaluateStationBuyOffers(stationContracts).Available)
        throw new Exception("Station buy offers must reject a changed resale direction.");
    checks += 16;
}

checks += CelestialLayoutTests.Run();
checks += OreDiscoveryTests.Run();
checks += OreDepositTests.Run();
checks += BodyEnvironmentTests.Run();
checks += AtmosphereSamplingAuditTests.Run();
checks += AtmosphereProviderTests.Run();
checks += ConnectedRailTests.Run();
checks += AsteroidTerrainEditAuditTests.Run();
checks += PersistentWaypointTests.Run();
checks += TerrainAnchorAuditTests.Run();
checks += TerrainAnchorStateTests.Run();
checks += TerrainSuspensionTests.Run();
checks += StockWreckSourceAuditTests.Run();
checks += StockWreckClaimJournalTests.Run();
checks += TerrainGenerationProviderTests.Run();
checks += GalaxyCatalogTests.Run();
checks += GalaxyCatalogPinTests.Run();
checks += TerrainPlacementTests.Run();
checks += GridDestructionTests.Run();
checks += GalaxyCatalogPreEntryTests.Run();
void Check(bool ok,string name) { checks++; if(!ok) throw new Exception(name); }
void Reject(Action action,string name) { checks++; try { action(); } catch(InvalidOperationException) { return; } throw new Exception(name); }
ContentRequest R(string key,int revision=1) => new(key,ResourceKind.Brick,revision);
var a = new ContentAllocator(8, new[]{0,1}, Array.Empty<int>());
var first = a.Apply(new[]{R("example.beta:b"),R("example.alpha:a")});
Check(first["example.alpha:a"] == 2 && first["example.beta:b"] == 3,"deterministic allocation");
a.Apply(new[]{R("example.beta:b")});
var json=a.ToJson();
var restored = ContentAllocator.FromJson(json,8,new[]{0,1},Array.Empty<int>());
Check(restored.Apply(new[]{R("example.alpha:a")})["example.alpha:a"] == 2,"tombstone restore");
var before=restored.ToJson();
Reject(()=>restored.Apply(new[]{R("example.alpha:a",2)}),"revision mismatch");
Check(before == restored.ToJson(),"revision failure mutated state");
Reject(()=>restored.Apply(new[]{R("example.alpha:a"),R("example.alpha:a")}),"duplicate request");
Check(before == restored.ToJson(),"duplicate failure mutated state");
var occupied=ContentAllocator.FromJson(before,8,new[]{0,1},new[]{2});
Reject(()=>occupied.Apply(new[]{R("example.alpha:a")}),"occupied persisted ID");
var small=new ContentAllocator(3,new[]{0,1},Array.Empty<int>());
var empty=small.ToJson();
Reject(()=>small.Apply(new[]{R("example.alpha:a"),R("example.beta:b")}),"exhaustion");
Check(empty==small.ToJson(),"exhaustion mutated state");
Reject(()=>ContentAllocator.FromJson("{broken",8,new[]{0,1},Array.Empty<int>()),"corrupt JSON");
Reject(()=>ContentAllocator.FromJson(json.Replace("\"id\":3","\"id\":2"),8,new[]{0,1},Array.Empty<int>()),"duplicate persisted ID");
Reject(()=>a.Apply(new[]{R("invalid:a")}),"invalid namespace");
var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
using(var framework=AssemblyDefinition.ReadAssembly(typeof(Sevo.Framework.Runtime.ContractAudit).Assembly.Location))
{
 var registrations=framework.MainModule.GetType("Sevo.Framework.Runtime.ContentRegistration").Methods;
 Check(registrations.Count(method=>method.IsPublic && method.Name=="RegisterFunctionalBlock")==2
  && registrations.Any(method=>method.IsPublic && method.Name=="RegisterFunctionalBlock" && method.Parameters.Count==6
   && method.Parameters.Any(parameter=>parameter.ParameterType.FullName=="System.Func`1<VoxelEngine.Brick>")),
  "functional registration preserves the caller-selected template API and adds a category overload");
 Check(framework.MainModule.GetType("Sevo.Framework.Runtime.StockRepairBeam")==null,
  "the framework must not contain the sample's template policy");
 Check(registrations.Any(method=>method.IsPublic && method.Name=="RegisterWeaponAttachmentReceiver"
   && method.Parameters.Select(p=>p.Name).SequenceEqual(new[]{"ownerId","localName","revision","displayName","selectTemplate","compatibleAttachmentKeys","behaviorFactory"}))
  && registrations.Any(method=>method.IsPublic && method.Name=="RegisterWeaponAttachment" && method.Parameters.Count==5)
  && framework.MainModule.GetType("Sevo.Framework.Runtime.WeaponAttachmentSlot").Properties
     .Select(property=>property.Name).SequenceEqual(new[]{"Index","Slot","AttachmentKey","Attachment"}),
  "weapon attachment declarations and snapshot preserve their public contract");
 Check(registrations.Any(method=>method.IsPublic && method.Name=="RegisterWeaponAttachment" && method.Parameters.Count==6
   && method.Parameters[5].ParameterType.FullName=="System.Func`1<Sevo.Framework.Runtime.WeaponAttachmentVisual>"),
  "a separate custom visual overload preserves the stock-only attachment API");
 Check(registrations.Any(method=>method.IsPublic && method.Name=="RegisterWeaponAttachmentReceiver" && method.Parameters.Count==8
   && method.Parameters[7].ParameterType.FullName=="System.Func`1<Sevo.Framework.Runtime.WeaponAttachmentVisual>"),
  "a separate host visual overload preserves the stock-only receiver API");
 Check(registrations.Any(method=>method.IsPublic && method.Name=="RegisterWeaponAttachmentReceiver" && method.Parameters.Count==10
   && method.Parameters[8].Name=="repeatLocalName"
   && method.Parameters[9].ParameterType.FullName=="System.Func`1<Sevo.Framework.Runtime.WeaponAttachmentVisual>"),
  "owned receiver repeats declare a durable companion name and separate visual selector");
 Check(registrations.Any(method=>method.IsPublic && method.Name=="RegisterWeaponAttachmentReceiver" && method.Parameters.Count==11
   && method.Parameters[10].Name=="modularSections" && method.Parameters[10].ParameterType.FullName=="System.Boolean"),
  "modular sections opt in without changing existing receiver declarations");
 Check(new[]{"RegisterFunctionalBlock","RegisterWeaponAttachment","RegisterWeaponAttachmentReceiver"}.All(name=>
   registrations.Any(method=>method.IsPublic && method.Name==name && method.Parameters.Any(parameter=>
     parameter.Name=="codexCategory" && parameter.ParameterType.FullName=="System.String")))
   && framework.MainModule.GetType("Sevo.Framework.Runtime.StockShape").Methods.Any(method=>method.IsPublic
     && method.Name=="CreateHull" && method.Parameters.Last().Name=="codexCategory"),
  "the supported brick factories expose native Codex category selection");
}
StorageConnectorAuditTests.Run(game.MainModule, Check);
NativeBlockVariantAuditTests.Run(game.MainModule, Check);
GridRecoveryAuditTests.Run(game.MainModule, Check);
TransferLinkAuditTests.Run(game.MainModule, Check);
var results = Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule);
Check(results["packedIds"].Available && results["lifecycle"].Available,"current game contracts");
var hullField=game.MainModule.GetType("VoxelEngine.TabHull").Fields.Single(field=>field.Name=="HULL_A");
hullField.Name="UnavailableHull";
var withoutLegacyHull=Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule);
Check(!withoutLegacyHull["stockHull"].Available && withoutLegacyHull["lifecycle"].Available
 && withoutLegacyHull["stockTemplateMaterials"].Available
 && Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available,
 "legacy hull availability is independent of generic functional registration");
hullField.Name="HULL_A";
var glowField=game.MainModule.GetType("VoxelEngine.Brick").Fields.Single(field=>field.Name=="hasGlowPaint");
glowField.Name="UnavailableGlowPaint";
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available
 && Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule).Values.All(result=>result.Available),
 "a changed template layout disables functional blocks before native registration");
glowField.Name="hasGlowPaint";
var functionalResult=Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule);
Check(functionalResult.Available,"current functional command contracts: "+functionalResult.Reason);
var eventGateResult=Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalEventGate(game.MainModule);
Check(eventGateResult.Available,"current held Event Gate contracts: "+eventGateResult.Reason);
var attachmentsResult=Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule);
Check(attachmentsResult.Available,"current native weapon attachment contracts: "+attachmentsResult.Reason);
var auditVisuals=typeof(Sevo.Framework.Runtime.ContractAudit).GetMethod("EvaluateAttachmentVisuals")
 ?? throw new Exception("Missing optional attachment visual audit");
CapabilityResult VisualCapability() => (CapabilityResult)auditVisuals.Invoke(null,new object[]{game.MainModule})!;
Check(VisualCapability().Available,"current native attachment visual contracts: "+VisualCapability().Reason);
var voxelMesh=game.MainModule.GetType("VoxelEngine.VoxelMesh");
var meshField=voxelMesh.Fields.Single(field=>field.Name=="Mesh");
meshField.Name="ChangedNativeMesh";
Check(!VisualCapability().Available && Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule).Available,
 "a changed mesh store disables custom visuals without disabling stock attachments");
meshField.Name="Mesh";
var offset=game.MainModule.GetType("VoxelEngine.Offset").Methods.Single(method=>method.Name=="GetFaceOffset" && method.Parameters.Count==2);
var averagingFactor=offset.Body.Instructions.Single(instruction=>instruction.OpCode==Mono.Cecil.Cil.OpCodes.Ldc_R4);
var originalAveragingFactor=averagingFactor.Operand;
averagingFactor.Operand=0.5f;
Check(!VisualCapability().Available && Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule).Available,
 "a changed native UV material calculation disables only the optional custom visual path");
averagingFactor.Operand=originalAveragingFactor;
var nativeAddonLink=game.MainModule.GetType("VoxelEngine.AddonCommand").Methods.Single(method=>method.Name=="Link");
nativeAddonLink.Name="UnsupportedLink";
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule).Available
 && Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available
 && Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalEventGate(game.MainModule).Available,
 "a changed attachment contract leaves other functional commands and inputs available");
nativeAddonLink.Name="Link";
var sideLayout=game.MainModule.GetType("VoxelEngine.SystemWeapon").Methods.Single(method=>method.Name=="SpawnSideAddon");
var halfGrid=sideLayout.Body.Instructions.First(instruction=>instruction.OpCode==Mono.Cecil.Cil.OpCodes.Ldc_I4_2);
halfGrid.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_1;
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule).Available
 && Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available,
 "a changed native slot layout disables attachment registration without gating ordinary functional blocks");
halfGrid.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_2;
var gateBinding=game.MainModule.GetType("VoxelEngine.CommandEventGate").Methods.Single(method=>method.Name=="SetStateData");
gateBinding.Name="UnsupportedStateBinding";
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalEventGate(game.MainModule).Available
 && Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available,
 "incompatible Event Gate reset only disables held Event Gate input");
gateBinding.Name="SetStateData";
var switchActivation=game.MainModule.GetType("VoxelEngine.CommandSwitch").Methods.Single(method=>method.Name=="Activate");
switchActivation.Name="UnsupportedActivate";
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available
 && Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule).Values.All(result=>result.Available),
 "incompatible switch only disables functional commands");
switchActivation.Name="Activate";
var lifecycleAwake=game.MainModule.GetType("ResourcesLoader").Methods.Single(x=>x.Name=="Awake");
var brickCall=lifecycleAwake.Body.Instructions.First(x=>x.Operand is MethodReference m && m.DeclaringType.FullName=="VoxelEngine.Brick" && m.Name=="LoadAllResourcesRuntime");
var allocatorCall=lifecycleAwake.Body.Instructions.First(x=>x.Operand is MethodReference m && m.DeclaringType.FullName=="Manager.GameResourcesManager" && m.Name=="AllocateResources");
(brickCall.Operand,allocatorCall.Operand)=(allocatorCall.Operand,brickCall.Operand);
Check(!Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule)["lifecycle"].Available,"wrong lifecycle order must be rejected");
(brickCall.Operand,allocatorCall.Operand)=(allocatorCall.Operand,brickCall.Operand);
game.MainModule.Mvid=Guid.NewGuid();
Check(Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule)["packedIds"].Available,"identity must not gate packed IDs");
Check(Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(game.MainModule).Available,"identity must not gate functional commands");
Check(Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalEventGate(game.MainModule).Available,"identity must not gate Event Gate input");
Check(Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule).Available,"identity must not gate weapon attachments");
Check(VisualCapability().Available,"identity must not gate attachment visuals");
var repeats=Sevo.Framework.Runtime.ContractAudit.EvaluateReceiverRepeats(game.MainModule);
Check(repeats.Available,"current receiver repeats survive an unrelated identity change: "+repeats.Reason);
var repeatUpdate=game.MainModule.GetType("VoxelEngine.Brick").Methods.Single(method=>method.Name=="UpdateRenderData"
  && method.Parameters[0].ParameterType.FullName=="VoxelEngine.BrickInstanceData");
var recursiveCall=repeatUpdate.Body.Instructions.Single(instruction=>instruction.Operand is MethodReference method
  && method.FullName==repeatUpdate.FullName);
var childFlag=recursiveCall.Previous;
var childFlagCode=childFlag.OpCode;
childFlag.OpCode=Mono.Cecil.Cil.OpCodes.Ldc_I4_0;
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateReceiverRepeats(game.MainModule).Available && VisualCapability().Available,
  "a changed repeat render call disables repeat support independently of add-on visuals");
childFlag.OpCode=childFlagCode;
var loadRepeats=game.MainModule.GetType("VoxelEngine.EntityBricks").Methods.Single(method=>method.Name=="RegisterBrickOnLoad");
var savedRepeatFlag=loadRepeats.Body.Instructions.First(instruction=>instruction.Operand is FieldReference field
  && field.Name=="isRepeatBrick");
var savedRepeatOperand=savedRepeatFlag.Operand;
savedRepeatFlag.Operand=game.MainModule.GetType("VoxelEngine.Brick").Fields.Single(field=>field.Name=="canPaint");
Check(!Sevo.Framework.Runtime.ContractAudit.EvaluateReceiverRepeats(game.MainModule).Available
  && Sevo.Framework.Runtime.ContractAudit.EvaluateWeaponAttachments(game.MainModule).Available,
  "a changed saved repeat contract disables owned repeats independently of ordinary attachment interaction");
savedRepeatFlag.Operand=savedRepeatOperand;
var mask=game.MainModule.GetType("VoxelEngine.BrickIndex").Fields.Single(x=>x.Name=="BRICK_MASK"); var originalMask=mask.Constant; mask.Constant=Convert.ToInt32(originalMask)/2;
var altered=Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule);
Check(!altered["packedIds"].Available && altered["lifecycle"].Available,"packed mask failure isolated");
mask.Constant=originalMask;
var awake=game.MainModule.GetType("ResourcesLoader").Methods.Single(x=>x.Name=="Awake");
var call=awake.Body.Instructions.First(x=>x.Operand is MethodReference m && m.DeclaringType.FullName=="VoxelEngine.Brick" && m.Name=="LoadAllResourcesRuntime");
call.OpCode=Mono.Cecil.Cil.OpCodes.Nop; call.Operand=null;
altered=Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule);
Check(altered["packedIds"].Available && !altered["lifecycle"].Available,"missing lifecycle call isolated");
var brick=game.MainModule.GetType("VoxelEngine.Brick"); var registry=brick.Fields.Single(x=>x.Name=="BRICK_INDEX");
brick.Fields.Remove(registry);
altered=Sevo.Framework.Runtime.ContractAudit.Evaluate(game.MainModule);
Check(!altered["brickRegistry"].Available && altered["packedIds"].Available,"missing registry field isolated");
var temp=Path.GetFullPath(Path.Combine(projectRoot,"tests","Sevo.Framework.Tests","test-output",Guid.NewGuid().ToString("N")));
if(!temp.StartsWith(Path.Combine(projectRoot,"tests","Sevo.Framework.Tests")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Test path escaped owned directory");
Directory.CreateDirectory(temp);
try {
 var profile=Path.Combine(temp,"state"); Directory.CreateDirectory(profile);
 File.WriteAllText(Path.Combine(profile,"sevo-runtime-profile.json"),"{\"schemaVersion\":1,\"profileId\":\""+Guid.NewGuid().ToString("D")+"\",\"gameDirectory\":\""+temp.Replace("\\","\\\\")+"\"}");
 Sevo.Framework.Runtime.ProfileProtocol.WriteStatus(profile,temp,"nonce",new[]{new CapabilityResult("audit",true,"ok")});
 var status=File.ReadAllText(Path.Combine(profile,"runtime-status.json"));
 Check(status.Contains("\"session\":\"nonce\"") && status.Contains("\"schemaVersion\":1") && status.Contains("\"processId\":"),"session handshake");
 Reject(()=>Sevo.Framework.Runtime.ProfileProtocol.WriteStatus(profile,Path.Combine(temp,"other"),"nonce",Array.Empty<CapabilityResult>()),"marker mismatch");
 var session = Sevo.Framework.Runtime.ProfileProtocol.Open(profile,temp,"nonce");
 var methods = session.GetType();
 var read = methods.GetMethod("ReadFeatureRegistry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
 var write = methods.GetMethod("WriteFeatureRegistry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
 Check(read != null && write != null,"feature registries must use the validated profile protocol");
 var fluidSpace = new ResourceSpace(ResourceKind.Fluid,8,new[]{0},Array.Empty<int>());
 var fluid = new ContentAllocator(new[]{fluidSpace},session.ProfileId);
 fluid.Apply(new[]{new ContentRequest("example.fluids:demo",ResourceKind.Fluid,1)});
 var payload = fluid.ToJson();
 write!.Invoke(session,new object[]{ResourceKind.Fluid,payload});
 Check((string?)read!.Invoke(session,new object[]{ResourceKind.Fluid}) == payload,"feature registry persists");
 Check(read!.Invoke(session,new object[]{ResourceKind.Schematic}) == null && session.ReadRegistry() == null,
   "feature namespaces leave existing brick storage untouched");
 var itemSpace = new ResourceSpace(ResourceKind.Item,8,new[]{0},Array.Empty<int>());
 var items = new ContentAllocator(new[]{itemSpace},session.ProfileId);
 var itemRequests = new[]{new ContentRequest("example.items:dust",ResourceKind.Item,1)};
 var itemId = items.Apply(itemRequests)["example.items:dust"];
 write!.Invoke(session,new object[]{ResourceKind.Item,items.ToJson()});
 var loadedItems=ContentAllocator.FromJson((string)read!.Invoke(session,new object[]{ResourceKind.Item})!,new[]{itemSpace});
 Check(loadedItems.Apply(itemRequests)["example.items:dust"] == itemId
   && (string?)read.Invoke(session,new object[]{ResourceKind.Fluid}) == payload,
   "inventory item IDs survive registry reload without touching fluid IDs");
 bool rejected = false;
 try { write!.Invoke(session,new object[]{ResourceKind.Schematic,payload}); }
 catch(System.Reflection.TargetInvocationException exception) when(exception.InnerException is InvalidOperationException) { rejected = true; }
 Check(rejected && read!.Invoke(session,new object[]{ResourceKind.Schematic}) == null,
   "cross-feature registry data is rejected before writing");
} finally { Directory.Delete(temp,true); }
checks += ExpandedTests.Run();
checks += RegistrationTests.Run();
checks += FunctionalLifetimeTests.Run();
checks += AttachmentMaterialAuditTests.Run();
checks += MaterialBindingTests.Run();
checks += AttachmentDefaultPaintAuditTests.Run();
checks += CodexCategoryAuditTests.Run();
checks += ItemCodexAuditTests.Run();
checks += FluidHookTests.Run();
checks += OreGenerationTests.Run();
using (var recipeGame = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll")) RecipeAuditTests.Run(recipeGame.MainModule);
Console.WriteLine($"{checks} checks passed");
