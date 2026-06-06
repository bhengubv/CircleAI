// ICoreMemoryStore.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Persistent store for tier-5 core memories — things the AI will not forget.
/// </summary>
public interface ICoreMemoryStore
{
    /// <summary>Adds a core memory.</summary>
    Task AddAsync(CoreMemory memory, CancellationToken ct = default);

    /// <summary>Returns a core memory by id, or null when not found.</summary>
    Task<CoreMemory?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the top-<paramref name="topK"/> core memories whose embedding
    /// is most similar (cosine) to <paramref name="queryEmbedding"/>. When
    /// the query is null, falls back to most-reinforced order.
    /// </summary>
    Task<IReadOnlyList<CoreMemory>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default);

    /// <summary>Returns all core memories in reinforcement order (most reinforced first).</summary>
    Task<IReadOnlyList<CoreMemory>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Increments <see cref="CoreMemory.ReinforcementCount"/> and bumps
    /// <see cref="CoreMemory.LastReinforcedUtc"/>. No-op when the id is unknown.
    /// </summary>
    Task ReinforceAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes a core memory.</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Total core memories currently stored.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
