using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UI.Codex;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registers independently identified fittings with stock pipe placement and native transport.</summary>
    public static class StorageConnectorRegistration
    {
        private static readonly Dictionary<int, Binding> Bindings = new Dictionary<int, Binding>();
        private static bool enabled;

        /// <summary>Call during the dependent plugin's Awake with a read-only stock pipe selector.</summary>
        public static void Register(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate)
            => RegisterCore(ownerId, localName, revision, displayName, selectTemplate, null);

        /// <summary>Uses the stock fitting's geometry and ports with an explicit item tube or fluid pipe role.</summary>
        public static void Register(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate, ItemStorageType transportType)
        {
            if (transportType != ItemStorageType.tube && transportType != ItemStorageType.fuelPipe)
                throw new ArgumentOutOfRangeException(nameof(transportType), "Only item tubes and fluid pipes are supported.");
            RegisterCore(ownerId, localName, revision, displayName, selectTemplate, transportType);
        }

        /// <summary>Declares an opt-in flush cargo fitting for the isolated creative placement adapter.</summary>
        public static void RegisterFlushItemFitting(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate)
            => RegisterCore(ownerId, localName, revision, displayName, selectTemplate, ItemStorageType.tube, flush: true);

        /// <summary>Declares a fitting with stock placement and selection, and without physical contact.</summary>
        public static void RegisterPassThrough(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate, ItemStorageType transportType)
        {
            if (transportType != ItemStorageType.tube && transportType != ItemStorageType.fuelPipe)
                throw new ArgumentOutOfRangeException(nameof(transportType), "Only item tubes and fluid pipes are supported.");
            RegisterCore(ownerId, localName, revision, displayName, selectTemplate, transportType, passThrough: true);
        }

        /// <summary>Declares a Creative flush item fitting with optional selectable pass-through geometry.</summary>
        public static void RegisterFlushItemFitting(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate, bool passThrough)
            => RegisterCore(ownerId, localName, revision, displayName, selectTemplate, ItemStorageType.tube,
                flush: true, passThrough: passThrough);

        private static void RegisterCore(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate, ItemStorageType? transportType, bool flush = false, bool passThrough = false)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (selectTemplate == null) throw new ArgumentNullException(nameof(selectTemplate));

            BrickPipe? source = null;
            ItemStorageType sourceType = default;
            ItemStorageType type = default;
            List<StorageAttachmentPoint>? ports = null;
            ResizeMode? resizeMode = null;
            object[]? recipe = null;
            ContentRegistration.Catalog.Register(ownerId, localName, revision, id =>
            {
                var template = source ?? throw new InvalidOperationException("Storage connector was not prepared.");
                var brick = new BrickPipe(id, displayName, template.nameMesh, resizeMode!, type, recipe!)
                {
                    colliderShape = template.colliderShape,
                    boundsOverride = template.boundsOverride,
                    boundsCulling = template.boundsCulling,
                    CollisionWidth = template.CollisionWidth,
                    CollisionScaleFactor = template.CollisionScaleFactor,
                    isDynamicGridSize = template.isDynamicGridSize,
                    forceLocalGrid = template.forceLocalGrid,
                    gridLevel = template.gridLevel,
                    minGridSize = template.minGridSize,
                    maxGridSize = template.maxGridSize,
                    gridLockOffset = template.gridLockOffset,
                    IsFacingForward = template.IsFacingForward,
                    NoMirroring = flush || template.NoMirroring,
                    scaleUV = template.scaleUV,
                    massForUnitShape = template.massForUnitShape,
                    useMaterial = template.useMaterial,
                    canPaint = template.canPaint,
                    hasGlowPaint = template.hasGlowPaint,
                    defaultPaint = template.defaultPaint,
                    PreviewPaint = template.PreviewPaint,
                    functionType = type == sourceType ? template.functionType : type == ItemStorageType.tube
                        ? BrickFunctionType.itemTransport : BrickFunctionType.fluidTransport,
                    systemProfile = type == sourceType ? template.systemProfile : default,
                    allowPipeOverlap = template.allowPipeOverlap
                };
                brick.Tab(CodexTab.PIPES);
                if (passThrough) SelectablePassThrough.Configure(brick);
                Bindings[id] = new Binding(brick, template, type, ports!);
                if (flush) FlushStoragePlacement.Bind(brick);
                return brick;
            }, () =>
            {
                if (!enabled) return "Native storage connectors are unavailable or disabled.";
                if (passThrough && !SelectablePassThrough.Enabled) return "Selectable pass-through geometry is unavailable or disabled.";
                if (selectTemplate() is not BrickPipe template || template.GetType() != typeof(BrickPipe)
                    || (uint)template.brickID >= Brick.BRICK_INDEX.Length
                    || !ReferenceEquals(Brick.BRICK_INDEX[template.brickID], template)
                    || Bindings.ContainsKey(template.brickID))
                    return "The selector must return a registered stock pipe.";
                if (!StockTemplateResources.IsTemplateAvailable(template, out var reason)) return reason;
                sourceType = template.GetItemStorageType(null!, 0);
                if (sourceType != ItemStorageType.tube && sourceType != ItemStorageType.fuelPipe)
                    return "The stock pipe is outside the supported item and fluid transport types.";
                type = transportType ?? sourceType;
                if (flush && !FlushStoragePlacement.CanRegister(template, type))
                    return "The audited creative flush-placement adapter or its stock template is unavailable.";
                var nativePorts = StorageAttachmentPoint.GetPoints(template.brickID, sourceType);
                (resizeMode, recipe) = StockTemplateResources.CopyRecipe(template);
                if (nativePorts.Count != 2 || resizeMode.canResize || !flush && (template.isTankJoin || template.forceLocalGrid)
                    || !template.skipHullCollisions || template.VoxelMaterials.Any(material =>
                        material.dynDataVert != DynData_Vert.none || material.dynDataPixel != DynData_Pixel.none))
                    return "Only fixed-size passive two-port fittings with static materials are supported.";
                ports = new List<StorageAttachmentPoint>(nativePorts);
                source = template;
                return null;
            }, ContentKind.StorageConnector);
        }

        internal static void Install(Harmony harmony)
        {
            if (!ItemStorageType.tube.IsItemStorage() || ItemStorageType.fuelPipe.IsItemStorage()
                || !ItemStorageType.tube.IsTubeOrHose() || !ItemStorageType.fuelPipe.IsTubeOrHose()
                || StorageConnectionLogic.AreTypeCompatible(ItemStorageType.tube, ItemStorageType.fuelPipe))
                throw new InvalidOperationException("Native storage no longer separates item and fluid connectors.");
            harmony.Patch(AccessTools.Method(typeof(StorageAttachmentPoint), nameof(StorageAttachmentPoint.GetPoints),
                new[] { typeof(int), typeof(ItemStorageType) }),
                postfix: new HarmonyMethod(typeof(StorageConnectorRegistration), nameof(ResolvePorts)));
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            Bindings.Clear();
        }

        internal static bool IsSupported(Brick brick)
            => TryGetBinding(brick.brickID, out var binding) && ReferenceEquals(brick, binding.Brick)
                && StockTemplateResources.IsSupported(brick, binding.Template);

        internal static bool TryLoad(Brick brick)
        {
            if (!TryGetBinding(brick.brickID, out var binding) || !ReferenceEquals(brick, binding.Brick)) return false;
            StockTemplateResources.Load(brick, binding.Template);
            return true;
        }

        private static void ResolvePorts(int __0, ItemStorageType __1, ref List<StorageAttachmentPoint> __result)
        {
            if (TryGetBinding(__0, out var binding) && binding.Type == __1) __result = binding.Ports;
        }

        private static bool TryGetBinding(int id, out Binding binding)
        {
            if (enabled && Bindings.TryGetValue(id, out var candidate)
                && (uint)id < Brick.BRICK_INDEX.Length && ReferenceEquals(Brick.BRICK_INDEX[id], candidate.Brick))
            {
                binding = candidate;
                return true;
            }
            binding = null!;
            return false;
        }

        private sealed class Binding
        {
            internal BrickPipe Brick { get; }
            internal BrickPipe Template { get; }
            internal ItemStorageType Type { get; }
            internal List<StorageAttachmentPoint> Ports { get; }

            internal Binding(BrickPipe brick, BrickPipe template, ItemStorageType type, List<StorageAttachmentPoint> ports)
            {
                Brick = brick;
                Template = template;
                Type = type;
                Ports = ports;
            }
        }
    }
}
