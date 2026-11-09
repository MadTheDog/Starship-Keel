using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace Sevo.Framework.Runtime
{
    internal readonly struct GalaxyCatalogAssetReference : IEquatable<GalaxyCatalogAssetReference>
    {
        internal string Asset { get; }
        internal long PathId { get; }

        internal GalaxyCatalogAssetReference(string asset, long pathId)
        {
            Asset = asset;
            PathId = pathId;
        }

        public bool Equals(GalaxyCatalogAssetReference other)
            => PathId == other.PathId && string.Equals(Asset, other.Asset, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => obj is GalaxyCatalogAssetReference other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Asset), PathId);
        public override string ToString() => Asset + ":" + PathId;
    }

    internal sealed class GalaxyCatalogAssetBinding
    {
        internal string TypeName { get; }
        internal GalaxyCatalogAssetReference Object { get; }
        internal IReadOnlyDictionary<string, GalaxyCatalogAssetReference> References { get; }

        internal GalaxyCatalogAssetBinding(string typeName, GalaxyCatalogAssetReference obj,
            IReadOnlyDictionary<string, GalaxyCatalogAssetReference> references)
        {
            TypeName = typeName;
            Object = obj;
            References = references;
        }
    }

    /// <summary>Discovers the stock serialized galaxy asset references without applying a catalog.</summary>
    internal static class GalaxyCatalogAssetContract
    {
        private const string Repository = "Manager.AssetRepoGalaxy";
        private const string Routes = "World.GalaxyWarpGateRoutes";
        private const string Map = "World.Galaxy.GalaxyMap";
        private const string Distribution = "World.GalaxyStarDistribution";
        private const string Behaviour = "UnityEngine.MonoBehaviour";
        private const string Scriptable = "UnityEngine.ScriptableObject";

        internal static Dictionary<string, Dictionary<string, int>> DiscoverSerializedFields(ModuleDefinition module)
        {
            (string Name, string Type) P(string name, string type) => (name, type);
            (string Name, string Type) Material(string name) => P(name, "UnityEngine.Material");
            (string Name, string Type) GameObject(string name) => P(name, "UnityEngine.GameObject");
            var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var repoBase = RequireType(module, "Manager.AssetRepo", Scriptable);
            if (repoBase.Fields.Any(IsSerialized))
                throw new InvalidOperationException("The inherited galaxy repository serialization prefix changed.");

            Add(Repository, repoBase.FullName, new[]
            {
                Material("material_Star"), Material("material_StarBase"), Material("material_StarHeight"),
                Material("material_WarpGateRoute"), Material("galaxyMaterial"), Material("material_Starfield"),
                Material("material_GalaxySkybox"), Material("material_GalaxyIsoLines"),
                P("starNameGenerator", "Common.BaseNameObject"), GameObject("widgetDataPrefab"), GameObject("widgetSimplePrefab"),
                P("distribution", Distribution), P("warpGateRoutes", Routes),
            }, "distribution", "warpGateRoutes");
            Add(Routes, Scriptable, new[] { P("distribution", Distribution) }, "distribution");
            Add(Map, Behaviour, new[] { GameObject("galaxyObject"), P("holomapCamera", "UnityEngine.Camera"),
                P("starDistribution", Distribution) }, "starDistribution");
            Add(Distribution, Scriptable, Array.Empty<(string, string)>());
            return result;

            void Add(string owner, string baseType, (string Name, string Type)[] prefix, params string[] targets)
            {
                var fields = RequireType(module, owner, baseType).Fields.Where(IsSerialized).Take(prefix.Length).ToArray();
                if (!fields.Select(field => (field.Name, field.FieldType.FullName)).SequenceEqual(prefix)
                    || fields.Any(field => field.CustomAttributes.Any(attribute =>
                        attribute.AttributeType.FullName == "UnityEngine.SerializeReference")))
                    throw new InvalidOperationException("The serialized galaxy object-reference prefix changed for " + owner + ".");
                result.Add(owner, targets.ToDictionary(target => target,
                    target => Array.FindIndex(fields, field => field.Name == target) * 12, StringComparer.Ordinal));
            }
        }

        internal static List<GalaxyCatalogAssetBinding> ReadInstalled(ModuleDefinition module, string dataDirectory)
        {
            string root = ProfileProtocol.Canonical(dataDirectory);
            ProfileProtocol.NoReparse(root);
            var fieldOffsets = DiscoverSerializedFields(module);
            var assets = new Dictionary<string, GalaxyCatalogSerializedAsset>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (!Regex.IsMatch(name, @"\A(globalgamemanagers(\.assets)?|resources\.assets|level[0-9]+|sharedassets[0-9]+\.assets)\z",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                    if (assets.Count >= 128 || assets.ContainsKey(name))
                        throw new InvalidOperationException("The installed player asset set is oversized or ambiguous.");
                    assets.Add(name, new GalaxyCatalogSerializedAsset(file));
                }
                if (!assets.ContainsKey("globalgamemanagers") || !assets.ContainsKey("globalgamemanagers.assets")
                    || !assets.ContainsKey("resources.assets") || !assets.Keys.Any(name => name.StartsWith("level", StringComparison.OrdinalIgnoreCase))
                    || !assets.Keys.Any(name => name.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The installed scene and resource asset set is incomplete.");

                var scripts = new Dictionary<GalaxyCatalogAssetReference, string>();
                foreach (var asset in assets.Values)
                {
                    if (asset.ExternalSources.Any(source => !assets.ContainsKey(source)
                        && source != "Library/unity default resources" && source != "Resources/unity_builtin_extra"))
                        throw new InvalidOperationException("An installed asset has an unresolved external source.");
                    foreach (var item in asset.Objects)
                    {
                        if (item.Value.Class == GalaxyCatalogSerializedAsset.PrefabInstance)
                            throw new InvalidOperationException("Serialized prefab overrides require their own galaxy binding audit.");
                        if (item.Value.Class != GalaxyCatalogSerializedAsset.MonoScript) continue;
                        using var from = asset.OpenObject(item.Key);
                        GalaxyCatalogSerializedAsset.ReadAlignedString(from);
                        from.BaseStream.Position += 20; // execution order and script property hash
                        string className = GalaxyCatalogSerializedAsset.ReadAlignedString(from);
                        string space = GalaxyCatalogSerializedAsset.ReadAlignedString(from);
                        string assembly = GalaxyCatalogSerializedAsset.ReadAlignedString(from);
                        string type = space + "." + className;
                        if (assembly == module.Assembly.Name.Name && fieldOffsets.ContainsKey(type))
                            scripts.Add(new GalaxyCatalogAssetReference(asset.Name, item.Key), type);
                    }
                }
                if (scripts.Count != fieldOffsets.Count || !fieldOffsets.Keys.All(scripts.Values.Contains))
                    throw new InvalidOperationException("The installed galaxy script identities are missing or ambiguous.");

                var bindings = new List<GalaxyCatalogAssetBinding>();
                foreach (var asset in assets.Values)
                {
                    foreach (var item in asset.Objects)
                    {
                        if (item.Value.Class != GalaxyCatalogSerializedAsset.MonoBehaviour) continue;
                        if (item.Value.Size < 32)
                            throw new InvalidOperationException("An installed script object uses an unsupported stripped reference.");
                        using var header = asset.OpenObject(item.Key, 28);
                        header.BaseStream.Position = 16; // native object owner, enabled flag and padding
                        var script = asset.ReadReference(header, allowNull: true);
                        if (!scripts.TryGetValue(script, out string? type)) continue;
                        using var from = asset.OpenObject(item.Key);
                        from.BaseStream.Position = 28;
                        GalaxyCatalogSerializedAsset.ReadAlignedString(from);
                        long start = from.BaseStream.Position;
                        var references = new Dictionary<string, GalaxyCatalogAssetReference>(StringComparer.Ordinal);
                        foreach (var field in fieldOffsets[type])
                        {
                            from.BaseStream.Position = start + field.Value;
                            references.Add(field.Key, asset.ReadReference(from));
                        }
                        bindings.Add(new GalaxyCatalogAssetBinding(type,
                            new GalaxyCatalogAssetReference(asset.Name, item.Key), references));
                    }
                }
                return bindings;
            }
            finally
            {
                foreach (var asset in assets.Values) asset.Dispose();
            }
        }

        internal static GalaxyCatalogAssetReference VerifyBindings(IReadOnlyList<GalaxyCatalogAssetBinding> bindings)
        {
            var repository = Single(Repository);
            var routes = Single(Routes);
            var stock = Single(Distribution);
            var maps = bindings.Where(binding => binding.TypeName == Map).ToArray();
            if (maps.Length == 0 || !PointsTo(repository, "distribution", stock.Object)
                || !PointsTo(repository, "warpGateRoutes", routes.Object) || !PointsTo(routes, "distribution", stock.Object)
                || maps.Any(binding => !PointsTo(binding, "starDistribution", stock.Object)))
                throw new InvalidOperationException("Serialized map, routes and repository do not share the same native galaxy catalog.");
            return stock.Object;

            GalaxyCatalogAssetBinding Single(string type)
            {
                var found = bindings.Where(binding => binding.TypeName == type).ToArray();
                if (found.Length != 1) throw new InvalidOperationException("Missing or ambiguous serialized galaxy object " + type + ".");
                return found[0];
            }
        }

        private static bool PointsTo(GalaxyCatalogAssetBinding binding, string field, GalaxyCatalogAssetReference expected)
            => binding.References.TryGetValue(field, out var target) && target.Equals(expected);

        private static TypeDefinition RequireType(ModuleDefinition module, string owner, string baseType)
        {
            var type = module.GetType(owner);
            if (type?.BaseType?.FullName != baseType)
                throw new InvalidOperationException("The serialized galaxy object base type changed for " + owner + ".");
            return type;
        }

        private static bool IsSerialized(FieldDefinition field)
            => !field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized
                && (field.IsPublic || field.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "UnityEngine.SerializeField"
                    || attribute.AttributeType.FullName == "UnityEngine.SerializeReference"));
    }
}
