// NullImplementations.cs — (2.9.0)

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Testing;

public sealed class NullSnapshotComparer : ISnapshotComparer
{
    public static readonly NullSnapshotComparer Instance = new();
    public string BackendId => "null";
    public ValueTask<SnapshotDiff> CompareAsync(string testId, string actual, CancellationToken ct = default)
        => ValueTask.FromResult(new SnapshotDiff(false, "NullSnapshotComparer — no golden store wired."));
}

public sealed class NullGoldenStore : IGoldenStore
{
    public static readonly NullGoldenStore Instance = new();
    public string BackendId => "null";
    public ValueTask<string?> ReadAsync(string testId, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    public ValueTask WriteAsync(string testId, string g, CancellationToken ct = default) => ValueTask.CompletedTask;
}
