using System;
using System.Collections.Generic;

namespace Sevo.Framework.Runtime
{
    internal static class TerrainEditBatch
    {
        internal static void Commit<T>(IReadOnlyList<T> nodes, Action<T> queue, Action<T> write,
            Action<T> restore, Func<T, string> describe)
        {
            foreach (var node in nodes)
            {
                try { queue(node); }
                catch (Exception exception)
                {
                    throw new InvalidOperationException("Scheduling failed at " + describe(node)
                        + " before density writes: " + exception.Message, exception);
                }
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                try { write(nodes[i]); }
                catch (Exception exception)
                {
                    var failedRestores = new List<string>();
                    // Include the failing copy because a native exception could follow a partial write.
                    for (int j = i; j >= 0; j--)
                    {
                        try { restore(nodes[j]); }
                        catch (Exception rollback)
                        {
                            failedRestores.Add(describe(nodes[j]) + " (" + rollback.Message + ")");
                        }
                    }

                    string recovery = failedRestores.Count == 0
                        ? "restored " + (i + 1) + " attempted node(s) before leaving the game-thread phase"
                        : "rollback incomplete at " + string.Join(", ", failedRestores);
                    throw new InvalidOperationException("Density write failed at " + describe(nodes[i]) + " ("
                        + exception.Message + "). " + recovery + ".", exception);
                }
            }
        }
    }
}
