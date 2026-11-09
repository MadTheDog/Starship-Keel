using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UI.Codex;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registers a new content identity backed by an audited stock command.</summary>
    public static class NativeBlockVariantRegistration
    {
        private static readonly ConditionalWeakTable<Brick, Brick> Templates = new ConditionalWeakTable<Brick, Brick>();
        private static bool enabled;

        public static void Register<T>(string ownerId, string localName, int revision, string displayName,
            Func<Brick?> selectTemplate, string codexCategory) where T : Command, new()
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (selectTemplate == null) throw new ArgumentNullException(nameof(selectTemplate));
            ContentRegistration.RequireCodexCategory(codexCategory);

            CommandBrick<T>? source = null;
            ResizeMode? mode = null;
            object[]? recipe = null;
            object[]? commandArgs = null;
            CodexTab? tab = null;
            ContentRegistration.Catalog.Register(ownerId, localName, revision, id =>
            {
                var template = source ?? throw new InvalidOperationException("Native variant was not prepared.");
                var brick = new CommandBrick<T>(id, displayName, template.nameMesh, mode!, recipe!)
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
                    type = template.type,
                    functionType = template.functionType,
                    systemProfile = template.systemProfile,
                    skipHullCollisions = template.skipHullCollisions
                };
                brick.SetCommandArgs(commandArgs!);
                brick.Tab(tab!);
                Templates.Add(brick, template);
                return brick;
            }, () =>
            {
                if (!enabled) return "Native block variants are unavailable or disabled.";
                if (selectTemplate() is not CommandBrick<T> template || template.GetType() != typeof(CommandBrick<T>)
                    || (uint)template.brickID >= Brick.BRICK_INDEX.Length
                    || !ReferenceEquals(Brick.BRICK_INDEX[template.brickID], template)
                    || Templates.TryGetValue(template, out _))
                    return "The selector must return a registered stock command template.";
                if (template.isDynDataCollider || template.isConveyorBelt || template.isPipeSeeThrough
                    || template.isFuelSeeThrough || template.forceLocalGrid || template.minGridSize > template.maxGridSize)
                    return "The command requires an unsupported collider or placement contract.";
                if (!StockTemplateResources.IsTemplateAvailable(template, out var reason)) return reason;

                var argsField = typeof(CommandBrick<T>).GetField("commandArgs", BindingFlags.Instance | BindingFlags.NonPublic);
                if (argsField?.GetValue(template) is not object[] args || !SupportsCommand(template, args))
                    return "The command template does not match an audited variant adapter.";
                tab = Plugin.ResolveCodexCategory(codexCategory, CodexTab.COMMANDS, ownerId + ":" + localName);
                (mode, recipe) = StockTemplateResources.CopyRecipe(template);
                commandArgs = (object[])args.Clone();
                source = template;
                return null;
            }, ContentKind.NativeBlockVariant);
        }

        private static bool SupportsCommand(Brick template, object[] args)
        {
            if (template is CommandBrick<SystemMagLock>)
                return args.Length == 1 && args[0] is float limit && !float.IsNaN(limit) && limit > 0f;
            if (template is CommandBrick<EnterCommandSeat>)
                return !ReferenceEquals(template, TabInterior.BED) && !ReferenceEquals(template, TabInterior.SEAT_STANDING)
                    && !ReferenceEquals(template, TabInterior.SEAT_SADDLE)
                    && args.Length == 2 && Array.TrueForAll(args, value => value is float offset
                        && !float.IsNaN(offset) && !float.IsInfinity(offset));
            return false;
        }

        internal static void Enable() => enabled = true;
        internal static void Disable() => enabled = false;

        internal static bool IsSupported(Brick brick)
            => Templates.TryGetValue(brick, out var template) && StockTemplateResources.IsSupported(brick, template);

        internal static bool TryLoad(Brick brick)
        {
            if (!Templates.TryGetValue(brick, out var template)) return false;
            StockTemplateResources.Load(brick, template);
            return true;
        }
    }
}
