using BepInEx;
using Sevo.Framework.Runtime;
using VoxelEngine;

namespace Sevo.Example.FlushStorageProbe
{
    [BepInPlugin(Id, "Keel Flush Storage Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.flushstorage";

        private void Awake()
        {
            StorageConnectorRegistration.RegisterFlushItemFitting(Id, "flush-cargo", 1, "Probe Flush Cargo Fitting",
                () => TabStorage.TANK_JOIN);
        }
    }
}
