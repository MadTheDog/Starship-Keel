using System.Text.Json;
using Sevo.Framework.Sdk;
using Sevo.Framework.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ExpandedTests
{
    public static int Run()
    {
        int count=0;
        void Check(bool value,string message) { count++; if(!value) throw new Exception(message); }
        void Reject(Action action) { count++; try {action();} catch(InvalidOperationException) {return;} throw new Exception("Expected rejection"); }
        ResourceSpace[] Spaces(int capacity=8) => new[]{new ResourceSpace(ResourceKind.Brick,capacity,new[]{0,1},Array.Empty<int>()),new ResourceSpace(ResourceKind.CollisionShape,3,new[]{0},new[]{1})};
        ContentRequest R(string key,ResourceKind kind=ResourceKind.Brick,int revision=1)=>new(key,kind,revision);
        var allocator=new ContentAllocator(Spaces());
        var ids=allocator.Apply(new[]{R("example.alpha:block"),R("example.alpha:shape",ResourceKind.CollisionShape)});
        Check(ids["example.alpha:block"]==2 && ids["example.alpha:shape"]==2,"resource spaces must be independent");
        var before=allocator.ToJson();
        Reject(()=>allocator.Apply(new[]{R("example.alpha:shape",ResourceKind.CollisionShape),R("example.beta:shape",ResourceKind.CollisionShape)}));
        Check(before==allocator.ToJson(),"shape exhaustion changed whole batch");
        allocator.Apply(Array.Empty<ContentRequest>());
        Check(allocator.Apply(new[]{R("example.beta:b")})["example.beta:b"]==3,"removed ID reused");
        foreach(var key in new[]{"Example.mod:a","example.mod:../a","example.mod:","example.mod:a:b","example.mod:a\n"}) Reject(()=>allocator.Apply(new[]{R(key)}));
        Reject(()=>allocator.Apply(new[]{R("example.alpha:a",revision:0)}));
        Reject(()=>allocator.Apply(new[]{R("example.alpha:a",(ResourceKind)77)}));
        Reject(()=>new ContentAllocator(new[]{Spaces()[0],Spaces()[0]}));
        Reject(()=>new ResourceSpace(ResourceKind.Brick,3,new[]{0},new[]{3}));
        Reject(()=>new ContentAllocator(8,new[]{0},Array.Empty<int>()).Apply(new[]{R("example.alpha:a",ResourceKind.CollisionShape)}));
        Reject(()=>ContentAllocator.FromJson("null",Spaces()));
        Reject(()=>ContentAllocator.FromJson(allocator.ToJson()+"{}",Spaces()));
        Reject(()=>ContentAllocator.FromJson(before.Replace("\"owner\":\"example.alpha\"","\"owner\":\"example.other\""),Spaces()));
        Reject(()=>ContentAllocator.FromJson(before.Replace("\"schemaVersion\":1","\"schemaVersion\":2"),Spaces()));
        Reject(()=>ContentAllocator.FromJson(before,Spaces(2)));
        using(var game=AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll"))
        {
            var module=game.MainModule;
            Check(ContractAudit.Evaluate(module).Values.All(result=>result.Available),"all real contracts must pass");
            module.Mvid=Guid.NewGuid(); module.Assembly.Name.Version=new Version(90,80,70,60);
            Check(ContractAudit.Evaluate(module).Values.All(result=>result.Available),"identity-only changes must pass every capability");
            var native=module.GetType("VoxelEngine.Brick").Methods.Single(method=>method.Name=="LoadAllResourcesRuntime");
            var constant=native.Body.Instructions.First(); constant.Operand=512;
            Check(ContractAudit.ReadLimits(module).SafeBrickCapacity==512,"native capacity must bound allocations");
            constant.OpCode=OpCodes.Ldarg_0; constant.Operand=null;
            var audit=ContractAudit.Evaluate(module);
            Check(!audit["nativeResources"].Available && audit["packedIds"].Available && audit["lifecycle"].Available,"changed allocation isolated");
        }
        using(var game=AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll"))
        {
            var module=game.MainModule;
            var field=module.GetType("VoxelEngine.BrickProperties").Fields.Single(value=>value.Name=="isValid");
            field.FieldType=module.TypeSystem.Int32;
            var audit=ContractAudit.Evaluate(module);
            Check(!audit["nativeResources"].Available && audit["lifecycle"].Available && audit["packedIds"].Available,"native field type failure isolated");
            var ctor=module.GetType("VoxelEngine.Brick").Methods.Single(method=>method.IsConstructor && !method.IsStatic);
            ctor.Parameters[0].ParameterType=module.TypeSystem.Int64;
            audit=ContractAudit.Evaluate(module);
            Check(!audit["brickRegistry"].Available && audit["packedIds"].Available,"constructor signature failure isolated");
        }

        var temp=Path.GetFullPath(Path.Combine("tests","Sevo.Framework.Tests","test-output",Guid.NewGuid().ToString("N")));
        if(!temp.StartsWith(Path.GetFullPath(Path.Combine("tests","Sevo.Framework.Tests"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Test output escaped owned directory");
        Directory.CreateDirectory(temp);
        try
        {
            var state=Path.Combine(temp,"state"); Directory.CreateDirectory(state); var lineage=Guid.NewGuid();
            var marker=Path.Combine(state,"sevo-runtime-profile.json");
            File.WriteAllText(marker,JsonSerializer.Serialize(new{schemaVersion=1,profileId=lineage.ToString("N"),gameDirectory=temp}));
            var session=ProfileProtocol.Open(state,temp,"nonce-one");
            session.WriteStatus(new RuntimeSnapshot{ContractsValidated=true,ResourcesReady=true,Reason="Audit only"});
            using(var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(state,"runtime-status.json"))))
            {
                var value=doc.RootElement;
                Check(value.GetProperty("session").GetString()=="nonce-one" && value.GetProperty("processId").GetInt32()==Environment.ProcessId,"wrong handshake identity");
                Check(value.GetProperty("resourcesReady").GetBoolean() && !value.GetProperty("registrationAdmitted").GetBoolean(),"audit confused with content admission");
            }
            Reject(()=>ProfileProtocol.Open(state,temp,"../bad"));
            var registry=new ContentAllocator(Spaces(),lineage); registry.Apply(new[]{R("example.alpha:a")}); session.WriteRegistry(registry.ToJson());
            Check(session.ReadRegistry()==registry.ToJson(),"registry atomic persistence");
            Reject(()=>session.WriteRegistry(new ContentAllocator(Spaces()).ToJson()));
            File.WriteAllText(marker,JsonSerializer.Serialize(new{schemaVersion=1,profileId=Guid.NewGuid().ToString(),gameDirectory=temp}));
            Reject(()=>session.WriteStatus(new RuntimeSnapshot()));
        }
        finally { Directory.Delete(temp,true); }
        return count;
    }
}
