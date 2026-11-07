using BepInEx;
using Sevo.Framework.Runtime;

namespace Sevo.Example.Alpha
{
    [BepInPlugin("example.alpha", "Example Alpha Hull", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            ContentRegistration.Register("example.alpha", "hull", 1,
                id => StockShape.CreateHull(id, "Example Alpha Hull"));
            Logger.LogInfo("Declared example.alpha:hull. Factory runs only in managed experimental mode.");
        }
    }
}
