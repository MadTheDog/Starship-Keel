using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Adds orbital stations and deep-space points using a stock catalog template.</summary>
    public static class OrbitalPoints
    {
        private static readonly SortedDictionary<string, Point> Points = new SortedDictionary<string, Point>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MetaDataPath> AuthoredPaths = new Dictionary<string, MetaDataPath>(StringComparer.Ordinal);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static int gameThread;
        private static bool sealedRegistration;
        private static Action<string>? warning;

        public static bool IsAvailable { get; private set; }

        public static void Register(string ownerId, string localName, ulong systemSeed, ulong planetSeed,
            StationType template, string displayName, Vector3 offsetMeters)
            => RegisterPoint(ownerId, localName, systemSeed, planetSeed, template, displayName, offsetMeters, null, default);

        /// <summary>Uses a mod-owned native blueprint and explicit consortium instead of a stock template.</summary>
        public static void RegisterBlueprint(string ownerId, string localName, ulong systemSeed, ulong planetSeed,
            StationType type, ConsortiumType consortium, string displayName, Vector3 offsetMeters, MetaDataPath blueprint)
        {
            if (string.IsNullOrEmpty(blueprint.directory) || string.IsNullOrEmpty(blueprint.fileName))
                throw new ArgumentException("Supply an installed native blueprint path.", nameof(blueprint));
            if (type != StationType.pointOfInterest
                && StationWorldGeneration.FromModuleType(blueprint.metaData.stationModuleType, blueprint.metaData) != type)
                throw new ArgumentException("The blueprint's native root module does not match the station type.", nameof(blueprint));
            if (!Enum.IsDefined(typeof(ConsortiumType), consortium)) throw new ArgumentOutOfRangeException(nameof(consortium));
            RegisterPoint(ownerId, localName, systemSeed, planetSeed, type, displayName, offsetMeters, blueprint, consortium);
        }

        private static void RegisterPoint(string ownerId, string localName, ulong systemSeed, ulong planetSeed,
            StationType template, string displayName, Vector3 offsetMeters, MetaDataPath? blueprint, ConsortiumType consortium)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (sealedRegistration) throw new InvalidOperationException("Orbital point registration is closed.");
            if (systemSeed == 0 || planetSeed == 0) throw new ArgumentException("Supply a system and planet seed.");
            if (template != StationType.spaceStation && template != StationType.corpoHub
                && template != StationType.fuelHub && template != StationType.cargoCache
                && template != StationType.pointOfInterest)
                throw new ArgumentOutOfRangeException(nameof(template));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Supply a display name.", nameof(displayName));
            if (!(offsetMeters.sqrMagnitude > 0 && offsetMeters.sqrMagnitude <= 1e18f))
                throw new ArgumentOutOfRangeException(nameof(offsetMeters));
            Points.Add(key, new Point(systemSeed, planetSeed, template, displayName, new Vector3l(offsetMeters), blueprint, consortium));
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var source = AccessTools.DeclaredMethod(typeof(StationWorldGeneration), "GetStarSystemPOIs",
                new[] { typeof(StarData), typeof(List<AstronData>) });
            harmony.Patch(source, postfix: new HarmonyMethod(typeof(OrbitalPoints), nameof(AfterCatalog)) { priority = Priority.Last });
            var spawn = AccessTools.DeclaredMethod(typeof(SpaceStation), "SpawnSpaceStationOnPlanet");
            harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(OrbitalPoints), nameof(BeforeSpawn)),
                transpiler: new HarmonyMethod(typeof(OrbitalPoints), nameof(ReplaceBlueprintCall)));
            gameThread = Environment.CurrentManagedThreadId;
            warning = reportWarning;
            IsAvailable = true;
        }

        internal static void Disable()
        {
            IsAvailable = false;
            AuthoredPaths.Clear();
        }

        private static bool BeforeSpawn(BrickEntityPOIData __1)
        {
            if (!IsAvailable || string.IsNullOrEmpty(__1.sguid) || !AuthoredPaths.TryGetValue(__1.sguid, out var path)
                || File.Exists(path.blueprintFullName)) return true;
            if (Reported.Add(__1.sguid)) warning?.Invoke($"Orbital point {__1.sguid} skipped because its blueprint disappeared.");
            return false;
        }

        private static IEnumerable<CodeInstruction> ReplaceBlueprintCall(IEnumerable<CodeInstruction> instructions)
        {
            var native = AccessTools.DeclaredMethod(typeof(EntityMetaDataRepo), nameof(EntityMetaDataRepo.GetMetaDataPath),
                new[] { typeof(EntityMetaData), typeof(bool) });
            var code = instructions.ToList();
            var call = code.Single(instruction => instruction.Calls(native));
            call.opcode = OpCodes.Ldarg_1;
            call.operand = null;
            code.Insert(code.IndexOf(call) + 1,
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(OrbitalPoints), nameof(ResolveBlueprint))));
            return code;
        }

        private static MetaDataPath ResolveBlueprint(EntityMetaData metadata, bool installed, BrickEntityPOIData point)
            => IsAvailable && !string.IsNullOrEmpty(point.sguid) && AuthoredPaths.TryGetValue(point.sguid, out var path)
                ? path : EntityMetaDataRepo.GetMetaDataPath(metadata, installed);

        private static void AfterCatalog(StarData __0, List<AstronData> __1, ref List<BrickEntityPOIData> __result)
        {
            if (!IsAvailable || Points.Count == 0 || Environment.CurrentManagedThreadId != gameThread
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || __result == null || __1 == null)
                return;
            sealedRegistration = true;
            if (GameIntroductionResources.IsStartingSystem(__0)) return;
            var stock = __result;
            List<BrickEntityPOIData>? extended = null;
            foreach (var declaration in Points)
            {
                if (declaration.Value.SystemSeed != __0.seed) continue;
                try
                {
                    var point = CreatePoint(__0, __1, stock, extended ?? stock, declaration.Key, declaration.Value);
                    if (declaration.Value.Blueprint is MetaDataPath path) AuthoredPaths[point.sguid] = path;
                    (extended ??= new List<BrickEntityPOIData>(stock)).Add(point);
                }
                catch (Exception exception)
                {
                    if (Reported.Add(declaration.Key))
                        warning?.Invoke($"Orbital point {declaration.Key} skipped: {exception.Message}");
                }
            }
            if (extended != null) __result = extended;
        }

        private static BrickEntityPOIData CreatePoint(StarData star, List<AstronData> bodies,
            List<BrickEntityPOIData> stock, List<BrickEntityPOIData> occupied, string key, Point point)
        {
            int index = bodies.FindIndex(body => body.seed == point.PlanetSeed && body.orbitalBodyType == OrbitalBodyType.planet);
            if (index < 0 || bodies[index].id != index)
                throw new InvalidOperationException("The target planet is absent or has a different native index.");
            BrickEntityPOIData template;
            if (point.Blueprint is MetaDataPath path)
            {
                if (!File.Exists(path.blueprintFullName)) throw new InvalidOperationException("The mod blueprint is missing.");
                template = new BrickEntityPOIData
                {
                    starData = star, stationType = point.Template, consortiumType = point.Consortium, metaData = path.metaData
                };
            }
            else
            {
                int templateIndex = stock.FindIndex(entry => entry.stationType == point.Template && !entry.isStartingStation);
                if (templateIndex < 0) throw new InvalidOperationException("No eligible stock template exists in this system.");
                template = stock[templateIndex];
            }
            var center = bodies[index].systemPosition;
            Vector3l position;
            checked
            {
                position = new Vector3l(center.x + point.Offset.x, center.y + point.Offset.y, center.z + point.Offset.z);
            }
            float clearance = Mathf.Max(1_000f, template.metaData.boundSize.magnitude);
            foreach (var body in bodies)
            {
                float radius = body.hasRing ? Mathf.Max(body.radius, body.ringOuterRadius) : body.radius;
                if (!(Distance(position, body.systemPosition) > radius * 1.25f + clearance))
                    throw new InvalidOperationException("The point overlaps a celestial body or its clearance.");
            }
            string id = MakeId("keel-orbit-", star.seed, key);
            if (occupied.Any(entry => entry.sguid == id
                || !(Distance(entry.systemPosition, position) > clearance + entry.metaData.boundSize.magnitude)))
                throw new InvalidOperationException("The point overlaps another catalog entry or its identity.");
            template.planetId = index;
            template.sguid = id;
            template.name = point.DisplayName;
            template.systemPosition = position;
            return template;
        }

        private static float Distance(Vector3l a, Vector3l b) => (a - b).Magnitude;

        internal static string MakeId(string prefix, ulong seed, string key)
        {
            using var hash = SHA256.Create();
            byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(key));
            return prefix + seed.ToString("x16", CultureInfo.InvariantCulture) + "-"
                + BitConverter.ToString(digest, 0, 16).Replace("-", "").ToLowerInvariant();
        }

        private sealed class Point
        {
            internal readonly ulong SystemSeed;
            internal readonly ulong PlanetSeed;
            internal readonly StationType Template;
            internal readonly string DisplayName;
            internal readonly Vector3l Offset;
            internal readonly MetaDataPath? Blueprint;
            internal readonly ConsortiumType Consortium;

            internal Point(ulong systemSeed, ulong planetSeed, StationType template, string displayName, Vector3l offset,
                MetaDataPath? blueprint, ConsortiumType consortium)
            {
                SystemSeed = systemSeed;
                PlanetSeed = planetSeed;
                Template = template;
                DisplayName = displayName;
                Offset = offset;
                Blueprint = blueprint;
                Consortium = consortium;
            }
        }
    }
}
