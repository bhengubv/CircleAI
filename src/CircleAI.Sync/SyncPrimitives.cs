// SyncPrimitives.cs
//
// (3.3.0) Top-up: shared sync-state helpers — version-vector merge,
// last-writer-wins reconciliation.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Sync;

public sealed record VersionVector(IReadOnlyDictionary<string, long> Clocks);

public static class SyncReconciliation
{
    public static VersionVector Merge(VersionVector a, VersionVector b)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        var keys = a.Clocks.Keys.Union(b.Clocks.Keys);
        var merged = new Dictionary<string, long>();
        foreach (var k in keys)
        {
            var av = a.Clocks.TryGetValue(k, out var x) ? x : 0;
            var bv = b.Clocks.TryGetValue(k, out var y) ? y : 0;
            merged[k] = Math.Max(av, bv);
        }
        return new VersionVector(merged);
    }

    public static bool ADominatesB(VersionVector a, VersionVector b)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        var keys = a.Clocks.Keys.Union(b.Clocks.Keys);
        var anyStrictlyGreater = false;
        foreach (var k in keys)
        {
            var av = a.Clocks.TryGetValue(k, out var x) ? x : 0;
            var bv = b.Clocks.TryGetValue(k, out var y) ? y : 0;
            if (av < bv) return false;
            if (av > bv) anyStrictlyGreater = true;
        }
        return anyStrictlyGreater;
    }

    public static (DateTimeOffset Winner, T WinnerVal) LastWriterWins<T>((DateTimeOffset At, T Val) a, (DateTimeOffset At, T Val) b)
        => a.At >= b.At ? (a.At, a.Val) : (b.At, b.Val);
}
