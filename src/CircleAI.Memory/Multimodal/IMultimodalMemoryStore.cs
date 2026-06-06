// IMultimodalMemoryStore.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Multimodal;

/// <summary>
/// Persistent store of compressed multimodal memories.
/// </summary>
public interface IMultimodalMemoryStore
{
    /// <summary>Adds an entry. Duplicate SHA-256 hits should be handled via <see cref="GetByHashAsync"/>.</summary>
    Task AddAsync(MultimodalMemoryEntry entry, CancellationToken ct = default);

    /// <summary>Returns the entry with the given hash, or null if unknown.</summary>
    Task<MultimodalMemoryEntry?> GetByHashAsync(string sourceSha256, CancellationToken ct = default);

    /// <summary>
    /// Increments <see cref="MultimodalMemoryEntry.ReferenceCount"/> for the
    /// entry whose hash matches. No-op when the hash is unknown.
    /// </summary>
    Task ReinforceAsync(string sourceSha256, CancellationToken ct = default);

    /// <summary>
    /// Returns the top-<paramref name="topK"/> entries whose embedding is most
    /// similar (cosine) to <paramref name="queryEmbedding"/>. When the query is
    /// null, falls back to most-recent.
    /// </summary>
    Task<IReadOnlyList<MultimodalMemoryEntry>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default);

    /// <summary>Returns the most recent <paramref name="count"/> entries.</summary>
    Task<IReadOnlyList<MultimodalMemoryEntry>> GetRecentAsync(
        int count = 10, CancellationToken ct = default);

    /// <summary>Removes entries older than <paramref name="cutoff"/>. Returns count removed.</summary>
    Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Total entries currently stored.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
