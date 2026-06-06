// InMemoryStores.cs
//
// In-memory implementations of the four consolidation-tier stores. Used by
// the test suite, by edge devices that don't need persistence, and as the
// default that AddMemoryConsolidator() registers when no SQLite-backed
// implementation has been wired.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>In-memory <see cref="IDailyMemoryStore"/>.</summary>
public sealed class InMemoryDailyMemoryStore : IDailyMemoryStore
{
    private readonly ConcurrentDictionary<DateOnly, DailyMemorySummary> _store = new();

    public Task UpsertAsync(DailyMemorySummary summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _store[summary.Day] = summary;
        return Task.CompletedTask;
    }

    public Task<DailyMemorySummary?> GetAsync(DateOnly day, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(day, out var s) ? s : null);

    public Task<IReadOnlyList<DailyMemorySummary>> GetRangeAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
    {
        IReadOnlyList<DailyMemorySummary> list = _store.Values
            .Where(s => s.Day >= fromInclusive && s.Day <= toInclusive)
            .OrderBy(s => s.Day)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<int> PruneOlderThanAsync(DateOnly cutoff, CancellationToken ct = default)
    {
        var toRemove = _store.Keys.Where(d => d < cutoff).ToList();
        foreach (var d in toRemove) _store.TryRemove(d, out _);
        return Task.FromResult(toRemove.Count);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_store.Count);
}

/// <summary>In-memory <see cref="ISemanticMemoryStore"/>.</summary>
public sealed class InMemorySemanticMemoryStore : ISemanticMemoryStore
{
    private readonly List<SemanticMemoryCluster> _store = new();
    private readonly object _lock = new();

    public Task AddAsync(SemanticMemoryCluster cluster, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        lock (_lock) _store.Add(cluster);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SemanticMemoryCluster>> GetWeekAsync(
        DateOnly weekStartingMonday, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<SemanticMemoryCluster> list = _store
                .Where(c => c.WeekStartingMonday == weekStartingMonday)
                .OrderByDescending(c => c.TopicWeight)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<SemanticMemoryCluster>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IEnumerable<SemanticMemoryCluster> source = _store;
            if (queryEmbedding is null)
            {
                IReadOnlyList<SemanticMemoryCluster> recent = source
                    .OrderByDescending(c => c.GeneratedAtUtc)
                    .Take(topK)
                    .ToList();
                return Task.FromResult(recent);
            }

            IReadOnlyList<SemanticMemoryCluster> ranked = source
                .Where(c => c.CentroidEmbedding is not null)
                .Select(c => (c, score: CosineSimilarity.Score(queryEmbedding, c.CentroidEmbedding!)))
                .OrderByDescending(t => t.score)
                .Take(topK)
                .Select(t => t.c)
                .ToList();
            return Task.FromResult(ranked);
        }
    }

    public Task<int> PruneOlderThanAsync(DateOnly cutoff, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var removed = _store.RemoveAll(c => c.WeekStartingMonday < cutoff);
            return Task.FromResult(removed);
        }
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_store.Count);
    }
}

/// <summary>In-memory <see cref="IPersonaDeltaStore"/>.</summary>
public sealed class InMemoryPersonaDeltaStore : IPersonaDeltaStore
{
    private readonly List<PersonaDeltaSnapshot> _store = new();
    private readonly object _lock = new();

    public Task AddAsync(PersonaDeltaSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock) _store.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersonaDeltaSnapshot>> GetForUserAsync(
        string userId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<PersonaDeltaSnapshot> list = _store
                .Where(s => string.Equals(s.UserId, userId, StringComparison.Ordinal))
                .OrderBy(s => s.PeriodStart)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_store.Count);
    }
}

/// <summary>In-memory <see cref="ICoreMemoryStore"/>.</summary>
public sealed class InMemoryCoreMemoryStore : ICoreMemoryStore
{
    private readonly ConcurrentDictionary<Guid, CoreMemory> _store = new();

    public Task AddAsync(CoreMemory memory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _store[memory.Id] = memory;
        return Task.CompletedTask;
    }

    public Task<CoreMemory?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(id, out var m) ? m : null);

    public Task<IReadOnlyList<CoreMemory>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        if (queryEmbedding is null)
        {
            IReadOnlyList<CoreMemory> top = _store.Values
                .OrderByDescending(m => m.ReinforcementCount)
                .ThenByDescending(m => m.LastReinforcedUtc)
                .Take(topK)
                .ToList();
            return Task.FromResult(top);
        }

        IReadOnlyList<CoreMemory> ranked = _store.Values
            .Where(m => m.Embedding is not null)
            .Select(m => (m, score: CosineSimilarity.Score(queryEmbedding, m.Embedding!)))
            .OrderByDescending(t => t.score)
            .Take(topK)
            .Select(t => t.m)
            .ToList();
        return Task.FromResult(ranked);
    }

    public Task<IReadOnlyList<CoreMemory>> ListAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CoreMemory> list = _store.Values
            .OrderByDescending(m => m.ReinforcementCount)
            .ThenByDescending(m => m.LastReinforcedUtc)
            .ToList();
        return Task.FromResult(list);
    }

    public Task ReinforceAsync(Guid id, CancellationToken ct = default)
    {
        if (_store.TryGetValue(id, out var memory))
        {
            memory.ReinforcementCount++;
            memory.LastReinforcedUtc = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_store.TryRemove(id, out _));

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_store.Count);
}

/// <summary>Internal cosine similarity helper shared by the in-memory stores.</summary>
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
