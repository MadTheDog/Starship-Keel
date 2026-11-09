using Sevo.Framework.Runtime;

void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

string Fail(Action action)
{
    try { action(); }
    catch (InvalidOperationException failure) { return failure.Message; }
    throw new Exception("Expected terrain batch failure.");
}

int[] nodes = { 11, 12, 13 };
var trace = new List<string>();
var state = nodes.ToDictionary(id => id, _ => "original");
void Queue(int node) => trace.Add($"queue {node}");
void Write(int node)
{
    trace.Add($"write {node}");
    state[node] = "changed";
}
void Restore(int node)
{
    trace.Add($"restore {node}");
    state[node] = "original";
}
string Describe(int node) => $"node {node}";

string queueFailure = Fail(() => TerrainEditBatch.Commit(nodes, id =>
{
    Queue(id);
    if (id == 12) throw new Exception("queue injected");
}, Write, Restore, Describe));
Require(trace.SequenceEqual(new[] { "queue 11", "queue 12" }) && state.Values.All(value => value == "original"),
    "A later queue failure must happen before every density write.");
Require(queueFailure.Contains("node 12") && queueFailure.Contains("before density writes"),
    "Queue errors name the node and phase.");

trace.Clear();
string copyFailure = Fail(() => TerrainEditBatch.Commit(nodes, Queue, id =>
{
    Write(id);
    if (id == 12) throw new Exception("copy injected after mutation");
}, Restore, Describe));
Require(trace.SequenceEqual(new[]
{
    "queue 11", "queue 12", "queue 13", "write 11", "write 12", "restore 12", "restore 11"
}) && state.Values.All(value => value == "original"),
    "Restore the failing node and all earlier copies in reverse order.");
Require(copyFailure.Contains("node 12") && copyFailure.Contains("restored 2"),
    "Copy failure reports successful rollback and the failed node.");

trace.Clear();
string restoreFailure = Fail(() => TerrainEditBatch.Commit(nodes, Queue, id =>
{
    Write(id);
    if (id == 12) throw new Exception("copy injected");
}, id =>
{
    if (id == 12) throw new Exception("restore injected");
    Restore(id);
}, Describe));
Require(state[11] == "original" && state[12] == "changed" && state[13] == "original",
    "A failed restore cannot skip restoring any other attempted node.");
Require(restoreFailure.Contains("rollback incomplete") && restoreFailure.Contains("node 12")
    && restoreFailure.Contains("restore injected"), "Failed restores are identified precisely.");

trace.Clear();
TerrainEditBatch.Commit(nodes, Queue, Write, Restore, Describe);
Require(trace.SequenceEqual(new[] { "queue 11", "queue 12", "queue 13", "write 11", "write 12", "write 13" }),
    "All queues are prepared before successful writes.");
Require(state.Values.All(value => value == "changed"), "Successful commit publishes every node.");
Console.WriteLine("Terrain batch queue ordering and injected rollback failure checks passed.");
