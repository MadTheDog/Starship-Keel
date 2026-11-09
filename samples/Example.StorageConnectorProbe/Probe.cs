using BepInEx;
using Sevo.Framework.Runtime;
using VoxelEngine;

namespace Sevo.Example.StorageConnectorProbe
{
    [BepInPlugin(Id, "Keel Storage Connector Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.storageconnector";

        private void Awake()
        {
            StorageConnectorRegistration.Register(Id, "short-item", 1, "Probe Short Conveyor",
                () => TabStorage.CONVEYOR_TUBE_SHORT);
            StorageConnectorRegistration.Register(Id, "angled-fluid", 1, "Probe Angled Fluid Pipe",
                () => TabStorage.PIPE_ANGLED);
        }
    }
}
