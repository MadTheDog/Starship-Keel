using BepInEx;
using Sevo.Framework.Runtime;
using VoxelEngine;

namespace Sevo.Example.NativeBlockVariantProbe
{
    [BepInPlugin(Id, "Keel Native Block Variant Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.nativeblockvariant";

        private void Awake()
        {
            NativeBlockVariantRegistration.Register<SystemMagLock>(Id, "clamp", 1, "Probe Grid Clamp",
                () => TabSystem.MAGLOCK, "CONSTRUCTION");
            NativeBlockVariantRegistration.Register<EnterCommandSeat>(Id, "seat", 1, "Probe Passenger Seat",
                () => TabInterior.SEAT_OTTOMAN, "INTERIOR");
        }
    }
}
