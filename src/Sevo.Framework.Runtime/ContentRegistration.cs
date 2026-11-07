using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public sealed class ContentDefinition
    {
        public string Key { get; }
        public int Revision { get; }
        internal Func<int,object> Factory { get; }
        internal ContentDefinition(string key,int revision,Func<int,object> factory)
        { Key=key; Revision=revision; Factory=factory; }
    }

    public sealed class RegistrationCatalog
    {
        private readonly object gate = new object();
        private readonly Dictionary<string,ContentDefinition> definitions = new Dictionary<string,ContentDefinition>(StringComparer.Ordinal);
        private bool sealedCatalog;
        public void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory)
        {
            var key=ownerId+":"+localName;
            ContentAllocator.ValidateKey(key);
            if(revision<1 || brickFactory==null) throw new InvalidOperationException("A positive revision and factory are required.");
            lock(gate)
            {
                if(sealedCatalog) throw new InvalidOperationException("Registration collection is closed; restart required.");
                if(definitions.ContainsKey(key)) throw new InvalidOperationException("Duplicate content key: "+key);
                definitions.Add(key,new ContentDefinition(key,revision,brickFactory));
            }
        }
        public IReadOnlyList<ContentDefinition> Seal()
        {
            lock(gate)
            {
                sealedCatalog=true;
                return Array.AsReadOnly(definitions.Values.OrderBy(value=>value.Key,StringComparer.Ordinal).ToArray());
            }
        }
    }

    /// <summary>Call from a dependent BepInEx plugin's Awake. Factories run only in explicit managed experimental mode.</summary>
    public static class ContentRegistration
    {
        internal static RegistrationCatalog Catalog { get; } = new RegistrationCatalog();
        public static void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory)
            => Catalog.Register(ownerId,localName,revision,brickFactory);
    }

    public interface INativeRegistry
    {
        int Capacity { get; }
        object? Read(int id);
        int IdOf(object definition);
        bool IsSupported(object definition);
    }

    /// <summary>Checks cooperating factories. It cannot sandbox arbitrary plugin code or reverse native side effects.</summary>
    public sealed class NativeRegistrationSession
    {
        private readonly INativeRegistry registry;
        private readonly Dictionary<int,object> owned = new Dictionary<int,object>();
        private bool attempted;
        public bool Failed { get; private set; }
        public IReadOnlyDictionary<int,object> Owned => new ReadOnlyDictionary<int,object>(owned);
        public NativeRegistrationSession(INativeRegistry registry) { this.registry=registry ?? throw new ArgumentNullException(nameof(registry)); }

        public void Commit(IReadOnlyList<ContentDefinition> definitions,ContentAllocator allocator,Action<string> persistRegistry)
        {
            if(attempted || Failed) throw new InvalidOperationException("Content registration cannot be retried in this process.");
            attempted=true;
            try
            {
                var ids=allocator.Apply(definitions.Select(definition=>new ContentRequest(definition.Key,ResourceKind.Brick,definition.Revision)));
                foreach(var id in ids.Values)
                    if(id<0 || id>=registry.Capacity || registry.Read(id)!=null) throw new InvalidOperationException("Reserved native slot became occupied: "+id);
                // Persist before any constructor can publish an ID into the native table.
                persistRegistry(allocator.ToJson());
                foreach(var definition in definitions)
                {
                    int id=ids[definition.Key];
                    var before=Enumerable.Range(0,registry.Capacity).Select(registry.Read).ToArray();
                    if(before[id]!=null) throw new InvalidOperationException("Native slot changed before factory: "+definition.Key);
                    var value=definition.Factory(id);
                    if(value==null || !registry.IsSupported(value) || registry.IdOf(value)!=id || !ReferenceEquals(registry.Read(id),value))
                        throw new InvalidOperationException("Factory did not register the expected ordinary brick: "+definition.Key);
                    if(registry.Capacity!=before.Length) throw new InvalidOperationException("Factory replaced the native registry capacity.");
                    for(int index=0;index<before.Length;index++)
                        if(index!=id && !ReferenceEquals(before[index],registry.Read(index)))
                            throw new InvalidOperationException("Factory changed an unreserved native slot: "+index);
                    owned.Add(id,value);
                }
                VerifyOwnership();
            }
            catch(Exception exception)
            {
                Failed=true;
                throw new InvalidOperationException("Content registration failed; restart required. Native mutations were not rolled back.",exception);
            }
        }
        public void VerifyOwnership()
        {
            if(Failed) throw new InvalidOperationException("Content session already failed; restart required.");
            try
            {
                foreach(var pair in owned)
                    if(pair.Key>=registry.Capacity || !ReferenceEquals(registry.Read(pair.Key),pair.Value))
                        throw new InvalidOperationException("Managed native ID was overwritten: "+pair.Key);
            }
            catch { Failed=true; throw; }
        }
    }
}
