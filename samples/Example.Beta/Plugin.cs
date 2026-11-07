using BepInEx;
using Sevo.Framework.Runtime;

namespace Sevo.Example.Beta
{
    [BepInPlugin("example.beta", "Example Beta Hull", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            ContentRegistration.Register("example.beta", "hull", 1,
                id => StockShape.CreateHull(id, "Example Beta Hull"));
            Logger.LogInfo("Declared example.beta:hull. Factory runs only in managed experimental mode.");
        }
    }
}
