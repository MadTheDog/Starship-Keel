using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    [DataContract]
    internal sealed class GalaxyCatalogProfilePin
    {
        private const int MaxWorlds = 128;
        private const int MaxBytes = 128 * 1024;

        [DataMember(Name = "schemaVersion", IsRequired = true)] private int schemaVersion;
        [DataMember(Name = "profileId", IsRequired = true)] private Guid profileId;
        [DataMember(Name = "nativeSaveRoot", IsRequired = true)] private string nativeSaveRoot;
        [DataMember(Name = "providerKey", IsRequired = true)] private string providerKey;
        [DataMember(Name = "revision", IsRequired = true)] private int revision;
        [DataMember(Name = "fingerprint", IsRequired = true)] private string fingerprint;
        [DataMember(Name = "worlds", IsRequired = true)] private List<WorldIdentity> worlds;

        [DataContract]
        private sealed class WorldIdentity
        {
            [DataMember(Name = "name", IsRequired = true)] public string Name { get; set; } = "";
            [DataMember(Name = "seed", IsRequired = true)] public ulong Seed { get; set; }
        }

        internal GalaxyCatalogProfilePin(Guid profileId, string nativeSaveRoot, string providerKey, int revision, string fingerprint)
        {
            schemaVersion = 1;
            this.profileId = profileId;
            this.nativeSaveRoot = ProfileProtocol.Canonical(nativeSaveRoot);
            this.providerKey = providerKey;
            this.revision = revision;
            this.fingerprint = fingerprint;
            worlds = new List<WorldIdentity>();
            Validate(profileId);
        }

        internal bool Matches(string root, string key, int requestedRevision, string digest)
            => string.Equals(nativeSaveRoot, ProfileProtocol.Canonical(root), StringComparison.OrdinalIgnoreCase)
            && string.Equals(providerKey, key, StringComparison.Ordinal)
            && revision == requestedRevision && string.Equals(fingerprint, digest, StringComparison.Ordinal);

        internal bool BelongsTo(Guid expectedProfile) => profileId == expectedProfile;

        internal bool ContainsWorld(string name, ulong seed)
            => worlds.Any(world => SameName(world.Name, name) && world.Seed == seed);

        internal GalaxyCatalogProfilePin WithWorld(string name, ulong seed)
        {
            RequireSafeName(name);
            var existing = worlds.SingleOrDefault(world => SameName(world.Name, name));
            if (existing != null)
            {
                if (existing.Seed == seed) return this;
                throw new InvalidOperationException("The native character name already belongs to another seed in this catalog profile.");
            }
            if (worlds.Count >= MaxWorlds)
                throw new InvalidOperationException("The galaxy catalog profile has reached its world identity limit.");
            var next = new GalaxyCatalogProfilePin(profileId, nativeSaveRoot, providerKey, revision, fingerprint);
            next.worlds.AddRange(worlds);
            next.worlds.Add(new WorldIdentity { Name = name, Seed = seed });
            return next;
        }

        internal byte[] Serialize(Guid expectedProfile)
        {
            Validate(expectedProfile);
            using var output = new MemoryStream();
            new DataContractJsonSerializer(typeof(GalaxyCatalogProfilePin)).WriteObject(output, this);
            return output.ToArray();
        }

        internal static GalaxyCatalogProfilePin Read(Stream input, Guid expectedProfile)
        {
            if (input.Length == 0 || input.Length > MaxBytes) throw new InvalidOperationException("Invalid galaxy catalog pin size.");
            var pin = (GalaxyCatalogProfilePin?)new DataContractJsonSerializer(typeof(GalaxyCatalogProfilePin)).ReadObject(input)
                ?? throw new InvalidOperationException("Missing galaxy catalog pin.");
            pin.Validate(expectedProfile);
            return pin;
        }

        private void Validate(Guid expectedProfile)
        {
            if (schemaVersion != 1 || profileId == Guid.Empty || profileId != expectedProfile)
                throw new InvalidOperationException("The galaxy catalog pin belongs to another profile or schema.");
            if (revision <= 0 || fingerprint?.Length != 64 || fingerprint.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw new InvalidOperationException("The galaxy catalog pin has an invalid revision or fingerprint.");
            ContentAllocator.ValidateKey(providerKey);
            if (!string.Equals(nativeSaveRoot, ProfileProtocol.Canonical(nativeSaveRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The galaxy catalog pin native save root is invalid.");

            if (worlds == null || worlds.Count > MaxWorlds)
                throw new InvalidOperationException("The galaxy catalog pin has an invalid world list.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var world in worlds)
            {
                if (world == null) throw new InvalidOperationException("The galaxy catalog pin contains a missing world.");
                RequireSafeName(world.Name);
                if (!names.Add(world.Name))
                    throw new InvalidOperationException("The galaxy catalog pin contains a repeated character name.");
            }
        }

        internal static void RequireSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new ArgumentException("A valid native character directory name is required.", nameof(name));
        }

        private static bool SameName(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    [DataContract]
    internal sealed class GalaxyCatalogNativeSaveBinding
    {
        internal const string FileName = "sevo-galaxy-save-root.json";

        [DataMember(Name = "schemaVersion", IsRequired = true)] private int SchemaVersion { get; set; }
        [DataMember(Name = "profileId", IsRequired = true)] private Guid ProfileId { get; set; }
        [DataMember(Name = "stateDirectory", IsRequired = true)] private string StateDirectory { get; set; } = "";
        [DataMember(Name = "nativeSaveRoot", IsRequired = true)] private string NativeSaveRoot { get; set; } = "";

        internal static string Verify(ProfileSession profile, string nativeSaveRoot, bool requireEmpty)
        {
            string root = ProfileProtocol.Canonical(nativeSaveRoot);
            string path = Path.Combine(root, FileName);
            var marker = new FileInfo(path);
            if (!marker.Exists || marker.Length == 0 || marker.Length > 65536 || (marker.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Use a dedicated native character save root with a launcher-created " + FileName + " marker.");
            using var input = File.OpenRead(path);
            var binding = (GalaxyCatalogNativeSaveBinding?)new DataContractJsonSerializer(typeof(GalaxyCatalogNativeSaveBinding)).ReadObject(input);
            if (binding == null || binding.SchemaVersion != 1 || binding.ProfileId != profile.ProfileId
                || !string.Equals(ProfileProtocol.Canonical(binding.StateDirectory), profile.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ProfileProtocol.Canonical(binding.NativeSaveRoot), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The native character save root is not bound to this managed profile.");

            if (requireEmpty && Directory.EnumerateFileSystemEntries(root)
                .Any(entry => !string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Use a new native character save root before pinning a galaxy catalog.");
            return root;
        }
    }
}
