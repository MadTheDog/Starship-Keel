using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Sevo.Framework.Runtime
{
    [DataContract]
    internal sealed class PersistentWaypointState
    {
        internal const int MaximumBytes = 1024 * 1024;
        private const int MaximumEntries = 1024;
        [DataMember(IsRequired = true)] private int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] private Guid Profile { get; set; }
        [DataMember(IsRequired = true)] private List<Entry> Entries { get; set; } = new List<Entry>();

        internal PersistentWaypointState(Guid profile) => Profile = profile;

        internal IReadOnlyList<PersistentWaypoint> List(string character, ulong world)
            => Entries.Where(entry => entry.Character == character && entry.World == world)
                .Select(entry => entry.Waypoint).OrderBy(marker => marker.Key, StringComparer.Ordinal).ToArray();

        internal void Upsert(string character, ulong world, PersistentWaypoint waypoint)
        {
            ValidateScope(character, world);
            waypoint.Validate();
            int index = Entries.FindIndex(entry => entry.Character == character && entry.World == world && entry.Waypoint.Key == waypoint.Key);
            var replacement = new Entry(character, world, waypoint);
            if (index >= 0) Entries[index] = replacement;
            else if (Entries.Count >= MaximumEntries) throw new InvalidOperationException("Persistent waypoint capacity reached.");
            else Entries.Add(replacement);
        }

        internal bool Delete(string character, ulong world, string key)
            => Entries.RemoveAll(entry => entry.Character == character && entry.World == world && entry.Waypoint.Key == key) != 0;

        internal byte[] Serialize()
        {
            using var output = new MemoryStream();
            new DataContractJsonSerializer(typeof(PersistentWaypointState)).WriteObject(output, this);
            if (output.Length > MaximumBytes) throw new InvalidOperationException("Persistent waypoint file exceeds its size limit.");
            return output.ToArray();
        }

        internal static PersistentWaypointState Read(byte[] bytes, Guid profile)
        {
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Oversized persistent waypoint file.");
            using var input = new MemoryStream(bytes, false);
            var value = new DataContractJsonSerializer(typeof(PersistentWaypointState)).ReadObject(input) as PersistentWaypointState;
            if (value == null || value.Version != 1 || value.Profile != profile || value.Entries == null
                || value.Entries.Count > MaximumEntries)
                throw new InvalidOperationException("Persistent waypoint schema, capacity or profile mismatch.");
            var keys = new HashSet<(string, ulong, string)>();
            foreach (var entry in value.Entries)
            {
                if (entry == null || entry.Waypoint == null) throw new InvalidOperationException("Missing persistent waypoint.");
                ValidateScope(entry.Character, entry.World);
                if (!keys.Add((entry.Character, entry.World, entry.Waypoint.Validate().Key)))
                    throw new InvalidOperationException("Duplicate persistent waypoint.");
            }
            return value;
        }

        private static void ValidateScope(string character, ulong world)
        {
            if (string.IsNullOrWhiteSpace(character) || character.Length > 128 || world == 0)
                throw new ArgumentException("Invalid persistent waypoint world.");
        }

        [DataContract]
        private sealed class Entry
        {
            internal Entry(string character, ulong world, PersistentWaypoint waypoint)
                => (Character, World, Waypoint) = (character, world, waypoint);
            [DataMember(IsRequired = true)] internal string Character { get; private set; }
            [DataMember(IsRequired = true)] internal ulong World { get; private set; }
            [DataMember(IsRequired = true)] internal PersistentWaypoint Waypoint { get; private set; }
        }
    }
}
