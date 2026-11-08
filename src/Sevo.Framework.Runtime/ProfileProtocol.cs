using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    [DataContract]
    internal sealed class ProfileMarker
    {
        [DataMember(Name="schemaVersion", IsRequired=true)] public int SchemaVersion { get; set; }
        [DataMember(Name="profileId", IsRequired=true)] public string ProfileId { get; set; } = "";
        [DataMember(Name="gameDirectory", IsRequired=true)] public string GameDirectory { get; set; } = "";
    }

    [DataContract]
    public sealed class StatusCapability
    {
        [DataMember(Name="name")] public string Name { get; set; } = "";
        [DataMember(Name="available")] public bool Available { get; set; }
        [DataMember(Name="reason")] public string Reason { get; set; } = "";
    }

    /// <summary>ResourcesReady concerns this resource cycle only, never save or network admission.</summary>
    [DataContract]
    public sealed class RuntimeSnapshot
    {
        [DataMember(Name="schemaVersion", Order=1)] public int SchemaVersion { get; set; } = 1;
        [DataMember(Name="session", Order=2)] public string Session { get; internal set; } = "";
        [DataMember(Name="processId", Order=3)] public int ProcessId { get; internal set; }
        [DataMember(Name="profilePath", Order=4)] public string ProfilePath { get; internal set; } = "";
        [DataMember(Name="profileId", Order=5)] public string ProfileId { get; internal set; } = "";
        [DataMember(Name="contractsValidated", Order=6)] public bool ContractsValidated { get; set; }
        [DataMember(Name="registrationAdmitted", Order=7)] public bool RegistrationAdmitted { get; set; }
        [DataMember(Name="resourcesReady", Order=8)] public bool ResourcesReady { get; set; }
        [DataMember(Name="failed", Order=9)] public bool Failed { get; set; }
        [DataMember(Name="reason", Order=10)] public string Reason { get; set; } = "";
        [DataMember(Name="mode", Order=11)] public string Mode { get; set; } = "audit";
        [DataMember(Name="brickCapacity", Order=12)] public int BrickCapacity { get; set; }
        [DataMember(Name="occupiedBricks", Order=13)] public int OccupiedBricks { get; set; }
        [DataMember(Name="shapeCapacity", Order=14)] public int ShapeCapacity { get; set; }
        [DataMember(Name="occupiedShapes", Order=15)] public int OccupiedShapes { get; set; }
        [DataMember(Name="registeredDefinitions", Order=16)] public int RegisteredDefinitions { get; set; }
        [DataMember(Name="resourceCycle", Order=17)] public int ResourceCycle { get; set; }
        [DataMember(Name="capabilities", Order=18)] public StatusCapability[] Capabilities { get; set; } = Array.Empty<StatusCapability>();
        [DataMember(Name="assemblyMvid", Order=19)] public string AssemblyMvid { get; set; } = "";
        [DataMember(Name="updatedUtc", Order=20)] public string UpdatedUtc { get; internal set; } = "";
        [DataMember(Name="registeredFunctionalBlocks", Order=21)] public int RegisteredFunctionalBlocks { get; set; }
    }

    public static class ProfileProtocol
    {
        public static ProfileSession Open(string stateDirectory, string gameDirectory, string session)
            => new ProfileSession(stateDirectory, gameDirectory, session);

        // Compatibility helper: observational status, never registration readiness.
        public static void WriteStatus(string stateDirectory, string gameDirectory, string session, IEnumerable<CapabilityResult> capabilities)
            => Open(stateDirectory, gameDirectory, session).WriteStatus(new RuntimeSnapshot
            {
                Reason = "Audit only; native readiness has not been observed.",
                Capabilities = capabilities.Select(result => new StatusCapability { Name=result.Name, Available=result.Available, Reason=result.Reason }).ToArray()
            });

        internal static string Canonical(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("An absolute profile/game path is required.");
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        internal static void NoReparse(string path)
        {
            for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Reparse path refused: " + current);
        }
    }

    /// <summary>Only fixed file names are read/written below a pre-existing marked directory.</summary>
    public sealed class ProfileSession
    {
        private readonly string gameDirectory;
        private readonly string session;
        public string DirectoryPath { get; }
        public Guid ProfileId { get; }
        internal ProfileSession(string stateDirectory, string gameDirectory, string session)
        {
            if (string.IsNullOrWhiteSpace(session) || session.Length > 128 || session.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new InvalidOperationException("Invalid launcher session.");
            DirectoryPath = ProfileProtocol.Canonical(stateDirectory);
            this.gameDirectory = ProfileProtocol.Canonical(gameDirectory);
            this.session = session;
            ProfileId = ReadMarker();
        }
        private string Child(string fileName)
        {
            // Callers never supply this name; the API exposes no arbitrary destination path.
            var path = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
            if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime state containment failure.");
            ProfileProtocol.NoReparse(path);
            return path;
        }
        private Guid ReadMarker()
        {
            if (!Directory.Exists(DirectoryPath)) throw new InvalidOperationException("Profile state directory is missing.");
            ProfileProtocol.NoReparse(DirectoryPath);
            ProfileProtocol.NoReparse(gameDirectory);
            var path = Child("sevo-runtime-profile.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidOperationException("Missing or oversized profile marker.");
            ProfileMarker marker;
            try
            {
                using (var input = File.OpenRead(path)) marker = (ProfileMarker)new DataContractJsonSerializer(typeof(ProfileMarker)).ReadObject(input)!;
            }
            catch (Exception exception) { throw new InvalidOperationException("Invalid profile marker.", exception); }
            if (marker == null || marker.SchemaVersion != 1 || !Guid.TryParse(marker.ProfileId, out var id) || id == Guid.Empty
                || !string.Equals(ProfileProtocol.Canonical(marker.GameDirectory), gameDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Profile marker does not match game directory or schema.");
            return id;
        }
        private void ValidateMarker()
        {
            if (ReadMarker() != ProfileId) throw new InvalidOperationException("Runtime profile ownership changed; restart required.");
        }
        internal string? ReadFeatureRegistry(ResourceKind kind) => ReadRegistryFile(FeatureRegistryName(kind), kind);

        internal void WriteFeatureRegistry(ResourceKind kind, string json)
        {
            var name = FeatureRegistryName(kind);
            ValidateMarker();
            ValidateLineage(json, kind);
            AtomicWrite(name, Encoding.UTF8.GetBytes(json));
        }

        private static string FeatureRegistryName(ResourceKind kind) => kind switch
        {
            ResourceKind.Fluid => "fluid-registry.json",
            ResourceKind.Schematic => "schematic-registry.json",
            ResourceKind.Item => "item-registry.json",
            _ => throw new InvalidOperationException("Unsupported feature registry.")
        };

        public string? ReadRegistry() => ReadRegistryFile("registry.json", null);

        private string? ReadRegistryFile(string name, ResourceKind? kind)
        {
            ValidateMarker(); var path = Child(name);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidOperationException("Oversized registry.");
            var json = File.ReadAllText(path); ValidateLineage(json, kind); return json;
        }
        private void ValidateLineage(string json, ResourceKind? kind = null)
        {
            var state = RegistryJson.Read(json);
            if (state.SchemaVersion != 1 || !Guid.TryParse(state.Lineage, out var lineage) || lineage != ProfileId)
                throw new InvalidOperationException("Registry belongs to another profile lineage.");
            if (kind.HasValue && state.Mappings.Any(mapping => mapping == null || mapping.Kind != kind.Value))
                throw new InvalidOperationException("Registry contains another feature's content kind.");
        }
        public void WriteRegistry(string json)
        { ValidateMarker(); ValidateLineage(json); AtomicWrite("registry.json", Encoding.UTF8.GetBytes(json)); }

        public void WriteStatus(RuntimeSnapshot snapshot)
        {
            ValidateMarker();
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            snapshot.Session = session;
            snapshot.ProcessId = Process.GetCurrentProcess().Id;
            snapshot.ProfilePath = DirectoryPath;
            snapshot.ProfileId = ProfileId.ToString("D");
            snapshot.UpdatedUtc = DateTime.UtcNow.ToString("O");
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RuntimeSnapshot)).WriteObject(stream, snapshot);
                AtomicWrite("runtime-status.json", stream.ToArray());
            }
        }
        private void AtomicWrite(string fileName, byte[] bytes)
        {
            ValidateMarker();
            var output = Child(fileName);
            var temporary = Child("." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                ValidateMarker(); Child(fileName);
                if (File.Exists(output)) File.Replace(temporary, output, null); else File.Move(temporary, output);
            }
            finally
            {
                // Do not follow a replaced directory during cleanup.
                ProfileProtocol.NoReparse(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
