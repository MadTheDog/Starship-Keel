using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Sevo.Framework.Sdk
{
    public enum ResourceKind { Brick = 1, CollisionShape = 2, Fluid = 3, Schematic = 4, Item = 5 }

    public sealed class ContentRequest
    {
        public string Key { get; }
        public ResourceKind Kind { get; }
        public int Revision { get; }
        public ContentRequest(string key, ResourceKind kind, int revision)
        { Key = key; Kind = kind; Revision = revision; }
    }

    /// <summary>A snapshot of one native resource space. Sets are defensively copied.</summary>
    public sealed class ResourceSpace
    {
        private readonly HashSet<int> reserved;
        private readonly HashSet<int> occupied;
        public ResourceKind Kind { get; }
        public int Capacity { get; }
        public ResourceSpace(ResourceKind kind, int capacity, IEnumerable<int> reserved, IEnumerable<int> occupied)
        {
            if (!Enum.IsDefined(typeof(ResourceKind), kind) || capacity < 1 || capacity > 1_000_000)
                throw new InvalidOperationException("Invalid resource kind or capacity.");
            Kind = kind;
            Capacity = capacity;
            this.reserved = new HashSet<int>(reserved ?? throw new ArgumentNullException(nameof(reserved)));
            this.occupied = new HashSet<int>(occupied ?? throw new ArgumentNullException(nameof(occupied)));
            if (this.reserved.Concat(this.occupied).Any(id => id < 0 || id >= capacity))
                throw new InvalidOperationException("Reserved or occupied ID outside its resource capacity.");
        }
        internal bool IsReserved(int id) => reserved.Contains(id);
        internal bool IsOccupied(int id) => occupied.Contains(id);
    }

    [DataContract]
    public sealed class Mapping
    {
        [DataMember(Name = "key", IsRequired = true, Order = 1)] public string Key { get; set; } = "";
        [DataMember(Name = "owner", IsRequired = true, Order = 2)] public string Owner { get; set; } = "";
        [DataMember(Name = "kind", IsRequired = true, Order = 3)] public ResourceKind Kind { get; set; }
        [DataMember(Name = "id", IsRequired = true, Order = 4)] public int Id { get; set; }
        [DataMember(Name = "revision", IsRequired = true, Order = 5)] public int Revision { get; set; }
        [DataMember(Name = "tombstone", IsRequired = true, Order = 6)] public bool Tombstone { get; set; }
        internal Mapping Copy(bool tombstone) => new Mapping { Key = Key, Owner = Owner, Kind = Kind, Id = Id, Revision = Revision, Tombstone = tombstone };
    }

    [DataContract]
    public sealed class RegistryState
    {
        [DataMember(Name = "schemaVersion", IsRequired = true, Order = 1)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Name = "lineage", IsRequired = true, Order = 2)] public string Lineage { get; set; } = Guid.NewGuid().ToString("D");
        [DataMember(Name = "mappings", IsRequired = true, Order = 3)] public List<Mapping> Mappings { get; set; } = new List<Mapping>();
    }

    public sealed class CapabilityResult
    {
        public string Name { get; }
        public bool Available { get; }
        public string Reason { get; }
        public CapabilityResult(string name, bool available, string reason)
        { Name = name; Available = available; Reason = reason; }
    }

    /// <summary>Atomic full desired-content snapshots. Absent mappings become permanent reservations (tombstones).</summary>
    public sealed class ContentAllocator
    {
        private static readonly Regex KeyPattern = new Regex(@"\A[a-z0-9]+(?:[.-][a-z0-9]+)+:[a-z0-9]+(?:[_-][a-z0-9]+)*\z", RegexOptions.CultureInvariant);
        private readonly Dictionary<ResourceKind, ResourceSpace> spaces;
        private RegistryState state;
        public Guid Lineage => Guid.Parse(state.Lineage);

        // The original convenience overload explicitly describes the Brick space only.
        public ContentAllocator(int capacity, IEnumerable<int> reserved, IEnumerable<int> occupied)
            : this(new[] { new ResourceSpace(ResourceKind.Brick, capacity, reserved, occupied) }) { }
        public ContentAllocator(IEnumerable<ResourceSpace> spaces, Guid? lineage = null)
            : this(spaces, new RegistryState { Lineage = (lineage ?? Guid.NewGuid()).ToString("D") }) { }
        private ContentAllocator(IEnumerable<ResourceSpace> spaces, RegistryState state)
        {
            this.spaces = new Dictionary<ResourceKind, ResourceSpace>();
            foreach (var space in spaces ?? throw new ArgumentNullException(nameof(spaces)))
            {
                if (space == null || this.spaces.ContainsKey(space.Kind))
                    throw new InvalidOperationException("Null or duplicate resource space.");
                this.spaces.Add(space.Kind, space);
            }
            if (this.spaces.Count == 0) throw new InvalidOperationException("At least one resource space is required.");
            this.state = state;
            ValidateState();
        }

        public static void ValidateKey(string key)
        {
            if (key == null || key.Length > 256 || !KeyPattern.IsMatch(key))
                throw new InvalidOperationException("Invalid namespaced content key: " + key);
        }
        private static string Owner(string key) => key.Substring(0, key.IndexOf(':'));

        private void ValidateState()
        {
            if (state == null || state.SchemaVersion != 1 || !Guid.TryParse(state.Lineage, out var lineage)
                || lineage == Guid.Empty || state.Mappings == null || state.Mappings.Count > 100_000)
                throw new InvalidOperationException("Invalid registry schema, lineage or mappings.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<(ResourceKind, int)>();
            foreach (var mapping in state.Mappings)
            {
                if (mapping == null) throw new InvalidOperationException("Null persisted mapping.");
                ValidateKey(mapping.Key);
                if (mapping.Owner != Owner(mapping.Key) || mapping.Revision < 1 || !spaces.TryGetValue(mapping.Kind, out var space)
                    || mapping.Id < 0 || mapping.Id >= space.Capacity || space.IsReserved(mapping.Id)
                    || !keys.Add(mapping.Key) || !ids.Add((mapping.Kind, mapping.Id)))
                    throw new InvalidOperationException("Invalid or duplicate persisted mapping: " + mapping.Key);
            }
        }

        public IReadOnlyDictionary<string, int> Apply(IEnumerable<ContentRequest> requests)
        {
            var batch = (requests ?? throw new ArgumentNullException(nameof(requests))).ToArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in batch)
            {
                if (request == null) throw new InvalidOperationException("Null content request.");
                ValidateKey(request.Key);
                if (!seen.Add(request.Key) || !spaces.ContainsKey(request.Kind) || request.Revision < 1)
                    throw new InvalidOperationException("Invalid or duplicate content request: " + request.Key);
            }
            var next = new RegistryState
            {
                Lineage = state.Lineage,
                Mappings = state.Mappings.Select(mapping => mapping.Copy(true)).ToList()
            };
            var byKey = next.Mappings.ToDictionary(mapping => mapping.Key, StringComparer.Ordinal);
            var used = new HashSet<(ResourceKind, int)>(next.Mappings.Select(mapping => (mapping.Kind, mapping.Id)));
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var request in batch.OrderBy(request => request.Key, StringComparer.Ordinal))
            {
                var space = spaces[request.Kind];
                if (byKey.TryGetValue(request.Key, out var mapping))
                {
                    if (mapping.Kind != request.Kind || mapping.Revision != request.Revision)
                        throw new InvalidOperationException("Kind or revision mismatch: " + request.Key);
                    if (space.IsOccupied(mapping.Id))
                        throw new InvalidOperationException("Persisted ID occupied: " + request.Key + " (" + mapping.Id + ")");
                    mapping.Tombstone = false;
                }
                else
                {
                    var id = 0;
                    while (id < space.Capacity && (space.IsReserved(id) || space.IsOccupied(id) || used.Contains((space.Kind, id)))) id++;
                    if (id == space.Capacity) throw new InvalidOperationException("Resource IDs exhausted: " + request.Kind);
                    mapping = new Mapping { Key = request.Key, Owner = Owner(request.Key), Kind = request.Kind, Id = id, Revision = request.Revision };
                    next.Mappings.Add(mapping);
                    used.Add((request.Kind, id));
                }
                result.Add(request.Key, mapping.Id);
            }
            state = next;
            return result;
        }

        public string ToJson() => RegistryJson.Write(state);
        public static ContentAllocator FromJson(string json, IEnumerable<ResourceSpace> spaces) => new ContentAllocator(spaces, RegistryJson.Read(json));
        public static ContentAllocator FromJson(string json, int capacity, IEnumerable<int> reserved, IEnumerable<int> occupied)
            => FromJson(json, new[] { new ResourceSpace(ResourceKind.Brick, capacity, reserved, occupied) });
    }

    public static class RegistryJson
    {
        public static RegistryState Read(string json)
        {
            try
            {
                if (json == null || json.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Missing or oversized registry JSON.");
                RequireSingleObject(json);
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max))
                {
                    var state = (RegistryState?)new DataContractJsonSerializer(typeof(RegistryState)).ReadObject(reader);
                    while (reader.Read())
                        if (reader.NodeType != XmlNodeType.Whitespace) throw new InvalidOperationException("Trailing registry data.");
                    return state ?? throw new InvalidOperationException("Null registry JSON.");
                }
            }
            catch (Exception exception) when (!(exception is InvalidOperationException))
            { throw new InvalidOperationException("Invalid registry JSON.", exception); }
        }
        private static void RequireSingleObject(string json)
        {
            int depth = 0;
            bool inString = false, escaped = false, started = false, ended = false;
            foreach (char character in json)
            {
                if (ended) { if (!char.IsWhiteSpace(character)) throw new InvalidOperationException("Trailing registry data."); continue; }
                if (!started)
                {
                    if (char.IsWhiteSpace(character)) continue;
                    if (character != '{') throw new InvalidOperationException("Registry must be one JSON object.");
                    started = true; depth = 1; continue;
                }
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }
                if (character == '"') inString = true;
                else if (character == '{' || character == '[') depth++;
                else if (character == '}' || character == ']') { depth--; if (depth == 0) ended = true; }
            }
            if (!ended || inString) throw new InvalidOperationException("Incomplete registry JSON.");
        }
        public static string Write(RegistryState state)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RegistryState)).WriteObject(stream, state);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
