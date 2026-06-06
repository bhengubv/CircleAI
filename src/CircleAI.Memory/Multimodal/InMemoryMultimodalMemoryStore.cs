// InMemoryMultimodalMemoryStore.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Multimodal;

/// <summary>In-memory <see cref="IMultimodalMemoryStore"/>.</summary>
public sealed class InMemoryMultimodalMemoryStore : IMultimodalMemoryStore
{
    private readonly ConcurrentDictionary<string, MultimodalMemoryEntry> _byHash = new(StringComparer.OrdinalIgnoreCase);

    public Task AddAsync(MultimodalMemoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.SourceSha256))
            throw new ArgumentException("SourceSha256 is required.", nameof(entry));
        _byHash[entry.SourceSha256] = entry;
        return Task.CompletedTask;
    }

    public Task<MultimodalMemoryEntry?> GetByHashAsync(string sourceSha256, CancellationToken ct = default) =>
        Task.FromResult(_byHash.TryGetValue(sourceSha256, out var e) ? e : null);

    public Task ReinforceAsync(string sourceSha256, CancellationToken ct = default)
    {
        if (_byHash.TryGetValue(sourceSha256, out var e)) e.ReferenceCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MultimodalMemoryEntry>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        if (queryEmbedding is null)
        {
            IReadOnlyList<MultimodalMemoryEntry> recent = _byHash.Values
                .OrderByDescending(e => e.RecordedAtUtc)
                .Take(topK)
                .ToList();
            return Task.FromResult(recent);
        }

        IReadOnlyList<MultimodalMemoryEntry> ranked = _byHash.Values
            .Where(e => e.Embedding is { Length: > 0 })
            .Select(e => (e, score: CosineSimilarity.Score(queryEmbedding, e.Embedding!)))
            .OrderByDescending(t => t.score)
            .Take(topK)
            .Select(t => t.e)
            .ToList();
        return Task.FromResult(ranked);
    }

    public Task<IReadOnlyList<MultimodalMemoryEntry>> GetRecentAsync(
        int count = 10, CancellationToken ct = default)
    {
        IReadOnlyList<MultimodalMemoryEntry> recent = _byHash.Values
            .OrderByDescending(e => e.RecordedAtUtc)
            .Take(count)
            .ToList();
        return Task.FromResult(recent);
    }

    public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var doomed = _byHash.Values.Where(e => e.RecordedAtUtc < cutoff).Select(e => e.SourceSha256).ToList();
        foreach (var h in doomed) _byHash.TryRemove(h, out _);
        return Task.FromResult(doomed.Count);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_byHash.Count);
}

/// <summary>Internal cosine helper for the multimodal in-memory store.</summary>
internal static class CosineSimilarity
{
    public static float Score(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom < double.Epsilon ? 0f : (float)(dot / denom);
    }
}
