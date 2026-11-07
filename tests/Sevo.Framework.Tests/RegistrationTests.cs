using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class RegistrationTests
{
    private sealed class Block { public int Id; public Block(int id) { Id=id; } }
    private sealed class Registry : INativeRegistry
    {
        public object?[] Values=new object?[8];
        public int Capacity=>Values.Length;
        public object? Read(int id)=>Values[id];
        public int IdOf(object value)=>((Block)value).Id;
        public bool IsSupported(object value)=>value is Block;
    }
    public static int Run()
    {
        int checks=0;
        void Check(bool condition) {checks++; if(!condition) throw new Exception("Registration assertion failed");}
        void Reject(Action action) { checks++; try {action();} catch(InvalidOperationException){return;} throw new Exception("Expected registration rejection"); }
        ContentAllocator Allocator()=>new(8,new[]{0,1},Array.Empty<int>());
        var registry=new Registry(); var catalog=new RegistrationCatalog();
        catalog.Register("example.alpha","a",1,id=>registry.Values[id]=new Block(id));
        Reject(()=>catalog.Register("example.alpha","a",1,id=>new Block(id)));
        var definitions=catalog.Seal(); Reject(()=>catalog.Register("example.beta","b",1,id=>new Block(id)));
        var session=new NativeRegistrationSession(registry); bool persisted=false;
        session.Commit(definitions,Allocator(),_=>persisted=true);
        Check(persisted && session.Owned.Count==1 && !session.Failed); session.VerifyOwnership();
        registry.Values[2]=new Block(2); Reject(()=>session.VerifyOwnership()); Check(session.Failed);
        var partialRegistry=new Registry(); var partialCatalog=new RegistrationCatalog();
        partialCatalog.Register("example.alpha","a",1,id=>partialRegistry.Values[id]=new Block(id));
        partialCatalog.Register("example.beta","b",1,id=>throw new InvalidOperationException("factory failure"));
        var partial=new NativeRegistrationSession(partialRegistry);
        Reject(()=>partial.Commit(partialCatalog.Seal(),Allocator(),_=>{}));
        Check(partial.Failed && partialRegistry.Values[2]!=null); // No fake rollback of native mutations.
        Reject(()=>partial.Commit(Array.Empty<ContentDefinition>(),Allocator(),_=>{}));
        var wrongRegistry=new Registry(); var wrongCatalog=new RegistrationCatalog();
        wrongCatalog.Register("example.alpha","a",1,id=> {wrongRegistry.Values[7]=new Block(7);return wrongRegistry.Values[id]=new Block(id);});
        var wrong=new NativeRegistrationSession(wrongRegistry);
        Reject(()=>wrong.Commit(wrongCatalog.Seal(),Allocator(),_=>{})); Check(wrong.Failed);
        var diskRegistry=new Registry(); var diskCatalog=new RegistrationCatalog(); bool invoked=false;
        diskCatalog.Register("example.alpha","a",1,id=>{invoked=true;return diskRegistry.Values[id]=new Block(id);});
        Reject(()=>new NativeRegistrationSession(diskRegistry).Commit(diskCatalog.Seal(),Allocator(),_=>throw new InvalidOperationException("disk failed")));
        Check(!invoked);
        return checks;
    }
}
