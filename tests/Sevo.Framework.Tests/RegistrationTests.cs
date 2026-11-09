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
        checks += VerifyMixedRegistrationAndReload();
        return checks;
    }

    private static int VerifyMixedRegistrationAndReload()
    {
        const int fixedId = 2;
        const int managedId = 3;
        const string owner = "example.saved-blocks";
        var legacyBlock = new Block(fixedId);
        var first = new Registry();
        first.Values[fixedId] = legacyBlock;
        var initial = new RegistrationCatalog();
        initial.Register(owner, "block", 1, id => first.Values[id] = new Block(id));
        string saved = "";
        new NativeRegistrationSession(first).Commit(initial.Seal(),
            new ContentAllocator(first.Capacity, new[] { 0, 1 }, new[] { fixedId }), json => saved = json);
        if (first.Values[managedId] is not Block || !ReferenceEquals(first.Values[fixedId], legacyBlock))
            throw new Exception("Managed allocation must skip an occupied legacy slot.");

        var occupied = new Registry();
        occupied.Values[managedId] = new Block(managedId);
        var conflict = new NativeRegistrationSession(occupied);
        bool persisted = false;
        try
        {
            conflict.Commit(Array.Empty<ContentDefinition>(), ContentAllocator.FromJson(saved, occupied.Capacity,
                new[] { 0, 1 }, new[] { managedId }), _ => persisted = true);
        }
        catch (InvalidOperationException error) when (error.InnerException?.Message.Contains(owner + ":block") == true) { }
        if (!conflict.Failed || persisted)
            throw new Exception("A missing managed owner must still reject a legacy occupant before persisting.");

        var reload = new Registry();
        var session = new NativeRegistrationSession(reload);
        session.Commit(Array.Empty<ContentDefinition>(),
            ContentAllocator.FromJson(saved, reload.Capacity, new[] { 0, 1 }, Array.Empty<int>()),
            json => saved = json);
        var restored = new Registry();
        var restoredCatalog = new RegistrationCatalog();
        restoredCatalog.Register(owner, "block", 1, id => restored.Values[id] = new Block(id));
        new NativeRegistrationSession(restored).Commit(restoredCatalog.Seal(),
            ContentAllocator.FromJson(saved, restored.Capacity, new[] { 0, 1 }, Array.Empty<int>()), _ => { });
        if (restored.Values[managedId] is not Block)
            throw new Exception("Reloading the original owner must restore its saved numeric ID.");

        reload.Values[fixedId] = legacyBlock;
        session.VerifyOwnership();
        reload.Values[managedId] = new Block(managedId);
        try { session.VerifyOwnership(); }
        catch (InvalidOperationException error) when (error.Message.Contains(owner + ":block")) { }
        if (!session.Failed)
            throw new Exception("Ownership verification must reject a late write to an inactive managed ID.");
        return 4;
    }
}
