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
 var functional=Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalCommands(current.MainModule);
 Console.WriteLine($"{functional.Name}: {functional.Available}; {functional.Reason}");
 var eventGate=Sevo.Framework.Runtime.ContractAudit.EvaluateFunctionalEventGate(current.MainModule);
 Console.WriteLine($"{eventGate.Name}: {eventGate.Available}; {eventGate.Reason}");
 var limits=Sevo.Framework.Runtime.ContractAudit.ReadLimits(current.MainModule);
 Console.WriteLine($"Safe brick bound={limits.SafeBrickCapacity}; packed={limits.PackedCapacity}; properties={limits.BrickPropertiesCapacity}; collider primary={limits.ColliderPrimaryCapacity}; collider data={limits.ColliderDataCapacity}; shape templates={limits.ShapeCapacity}");
 Console.WriteLine("Diagnostic MVID="+current.MainModule.Mvid+"; no identity gate");
 return;
}
int checks=0;
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
 Check(registrations.Count(method=>method.IsPublic && method.Name.StartsWith("RegisterFunctional",StringComparison.Ordinal))==1
  && registrations.Any(method=>method.IsPublic && method.Name=="RegisterFunctionalBlock"
   && method.Parameters.Any(parameter=>parameter.ParameterType.FullName=="System.Func`1<VoxelEngine.Brick>")),
  "functional registration exposes one caller-selected template API");
 Check(framework.MainModule.GetType("Sevo.Framework.Runtime.StockRepairBeam")==null,
  "the framework must not contain the sample's template policy");
}
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
} finally { Directory.Delete(temp,true); }
checks += ExpandedTests.Run();
checks += RegistrationTests.Run();
checks += FunctionalLifetimeTests.Run();
Console.WriteLine($"{checks} checks passed");
