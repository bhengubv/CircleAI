// ISemanticMemoryStore.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Persistent store for tier-3 semantic memory clusters.
/// </summary>
public interface ISemanticMemoryStore
{
    /// <summary>Adds a cluster.</summary>
    Task AddAsync(SemanticMemoryCluster cluster, CancellationToken ct = default);

    /// <summary>Returns all clusters for the given week (Monday-based).</summary>
    Task<IReadOnlyList<SemanticMemoryCluster>> GetWeekAsync(
        DateOnly weekStartingMonday, CancellationToken ct = default);

    /// <summary>
    /// Returns the top-<paramref name="topK"/> clusters whose centroid embedding
    /// is most similar (cosine) to <paramref name="queryEmbedding"/>. When the
    /// query is null, falls back to recency.
    /// </summary>
    Task<IReadOnlyList<SemanticMemoryCluster>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default);

    /// <summary>Removes clusters whose week start is before <paramref name="cutoff"/>.</summary>
    Task<int> PruneOlderThanAsync(DateOnly cutoff, CancellationToken ct = default);

    /// <summary>Total clusters currently stored.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
