using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UI.Codex;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registers independently identified fittings that preserve a stock pipe's transport and placement.</summary>
    public static class StorageConnectorRegistration
    {
        private static readonly Dictionary<int, Binding> Bindings = new Dictionary<int, Binding>();
        private static bool enabled;

        /// <summary>Call during the dependent plugin's Awake with a read-only stock pipe selector.</summary>
        public static void Register(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (selectTemplate == null) throw new ArgumentNullException(nameof(selectTemplate));

            BrickPipe? source = null;
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
                    gridLevel = template.gridLevel,
                    minGridSize = template.minGridSize,
                    maxGridSize = template.maxGridSize,
                    gridLockOffset = template.gridLockOffset,
                    IsFacingForward = template.IsFacingForward,
                    NoMirroring = template.NoMirroring,
                    scaleUV = template.scaleUV,
                    massForUnitShape = template.massForUnitShape,
                    useMaterial = template.useMaterial,
                    canPaint = template.canPaint,
                    hasGlowPaint = template.hasGlowPaint,
                    defaultPaint = template.defaultPaint,
                    PreviewPaint = template.PreviewPaint,
                    functionType = template.functionType,
                    systemProfile = template.systemProfile,
                    allowPipeOverlap = template.allowPipeOverlap
                };
                brick.Tab(CodexTab.PIPES);
                Bindings[id] = new Binding(brick, template, type, ports!);
                return brick;
            }, () =>
            {
                if (!enabled) return "Native storage connectors are unavailable or disabled.";
                if (selectTemplate() is not BrickPipe template || template.GetType() != typeof(BrickPipe)
                    || (uint)template.brickID >= Brick.BRICK_INDEX.Length
                    || !ReferenceEquals(Brick.BRICK_INDEX[template.brickID], template)
                    || Bindings.ContainsKey(template.brickID))
                    return "The selector must return a registered stock pipe.";
                if (!StockTemplateResources.IsTemplateAvailable(template, out var reason)) return reason;
                type = template.GetItemStorageType(null!, 0);
                if (type != ItemStorageType.tube && type != ItemStorageType.fuelPipe)
                    return "The stock pipe is outside the supported item and fluid transport types.";
                var nativePorts = StorageAttachmentPoint.GetPoints(template.brickID, type);
                (resizeMode, recipe) = StockTemplateResources.CopyRecipe(template);
                if (nativePorts.Count != 2 || resizeMode.canResize || template.isTankJoin || template.forceLocalGrid
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
