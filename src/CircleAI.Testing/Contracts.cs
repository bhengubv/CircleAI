// Contracts.cs — (2.9.0) Snapshot-testing contracts.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Testing;

public sealed record SnapshotDiff(bool Equal, string? Diff);

public interface ISnapshotComparer
{
    string BackendId { get; }
    ValueTask<SnapshotDiff> CompareAsync(string testId, string actual, CancellationToken ct = default);
}

public interface IGoldenStore
{
    string BackendId { get; }
    ValueTask<string?> ReadAsync(string testId, CancellationToken ct = default);
    ValueTask WriteAsync(string testId, string golden, CancellationToken ct = default);
}
