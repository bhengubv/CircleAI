// InMemoryTesting.cs
//
// (3.3.0) Real snapshot comparer + golden store. Comparer normalises
// line endings + trailing whitespace before diffing, so common
// platform churn doesn't false-positive.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Testing;

public sealed class InMemoryGoldenStore : IGoldenStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask<string?> ReadAsync(string testId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(testId)) throw new ArgumentException("testId required", nameof(testId));
        _items.TryGetValue(testId, out var g);
        return ValueTask.FromResult(g);
    }

    public ValueTask WriteAsync(string testId, string golden, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(testId)) throw new ArgumentException("testId required");
        if (golden is null) throw new ArgumentNullException(nameof(golden));
        _items[testId] = golden;
        return ValueTask.CompletedTask;
    }
}

public sealed class LineDiffSnapshotComparer : ISnapshotComparer
{
    private readonly IGoldenStore _store;
    public LineDiffSnapshotComparer(IGoldenStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));
    public string BackendId => "line-diff";

    public async ValueTask<SnapshotDiff> CompareAsync(string testId, string actual, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(testId)) throw new ArgumentException("testId required", nameof(testId));
        if (actual is null) throw new ArgumentNullException(nameof(actual));
        var golden = await _store.ReadAsync(testId, ct).ConfigureAwait(false);
        if (golden is null) return new SnapshotDiff(false, "(no golden)");
        var a = Normalise(actual);
        var g = Normalise(golden);
        if (string.Equals(a, g, StringComparison.Ordinal)) return new SnapshotDiff(true, null);
        return new SnapshotDiff(false, BuildDiff(g, a));
    }

    private static string Normalise(string s) => string.Join('\n', s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()));

    private static string BuildDiff(string expected, string actual)
    {
        var exp = expected.Split('\n');
        var act = actual.Split('\n');
        var sb = new StringBuilder();
        var n = Math.Max(exp.Length, act.Length);
        for (var i = 0; i < n; i++)
        {
            var e = i < exp.Length ? exp[i] : "";
            var a = i < act.Length ? act[i] : "";
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                sb.Append('-').Append(e).Append('\n');
                sb.Append('+').Append(a).Append('\n');
            }
        }
        return sb.ToString();
    }
}
