using System.Reflection;
using HarmonyLib;
using UI;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Opens the native view for the currently targeted, valid cargo command.</summary>
    public static class InventoryViewService
    {
        private static FieldInfo? windowInstance;

        internal static void Enable() => windowInstance = AccessTools.Field(typeof(UI_CommandGeneric), "instance");
        internal static void Disable() => windowInstance = null;

        public static bool CanOpen(ContextActionContext context)
            => TryResolveCargo(context, out _);

        public static bool TryOpen(ContextActionContext context)
        {
            if (!TryResolveCargo(context, out var cargo)) return false;
            cargo.OnKeyUp(context.Player);
            return true;
        }

        private static bool TryResolveCargo(ContextActionContext expected, out StorageCommandData cargo)
        {
            cargo = default;
            if (windowInstance == null || !ContextActionProvider.TryGetCurrent(out var current, out var command)
                || windowInstance.GetValue(null) is not UI_CommandGeneric window || window == null
                || !current.Matches(expected) || command is not StorageCommandData candidate || !candidate.isCargo
                || candidate.StorageId <= 0)
                return false;

            var storage = StorageCommon.CONTEXT;
            if (candidate.StorageId >= storage.StorageLength) return false;

            var data = storage.GetStorageData(candidate.StorageId);
            if (data.id != candidate.StorageId || data.type != candidate.ItemStorageType
                || data.brickInstanceId != current.Placement.brickData.instanceId
                || data.childrenEntityId != current.Entity.ChildrenID || data.parentEntityId != current.Entity.Parent.IndexID
                || !float.IsFinite(data.maximumVolume) || data.maximumVolume <= 0f)
                return false;

            cargo = candidate;
            return true;
        }
    }
}
