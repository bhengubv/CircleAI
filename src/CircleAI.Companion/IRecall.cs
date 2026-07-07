// IRecall.cs
//
// (M1) One recall surface over the companion's memory. An implementation may
// draw on fast episodic similarity, slow graph association, or both — callers
// depend only on this contract, not on which stores happen to be wired.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Domain;

namespace CircleAI.Companion;

/// <summary>(M1) Unified memory recall — the most relevant memories for a turn.</summary>
public interface IRecall
{
    /// <summary>
    /// Recall the <paramref name="topK"/> most relevant memories for the current turn.
    /// <paramref name="query"/> drives graph association; <paramref name="queryEmbedding"/>
    /// drives episodic cosine similarity (may be <c>null</c> → episodic recency fallback).
    /// </summary>
    Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string query,
        float[]? queryEmbedding,
        int topK = 5,
        CancellationToken ct = default);
}
