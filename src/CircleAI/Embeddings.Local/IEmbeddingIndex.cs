// IEmbeddingIndex.cs
//
// (RT-09b) Plug point for fast vector search backends. v2.0 only had
// the brute-force in-memory path baked into InMemoryEmbeddingStore.
// 2.1.0 lifts the search algorithm behind this interface so:
//   - InMemoryEmbeddingStore keeps its brute-force path (no backend),
//   - HnswEmbeddingStore (RT-09b) wraps the turbovec C bridge for
//     SIMD-blocked quantised search.
// Future: PgvectorEmbeddingIndex, FaissEmbeddingIndex, etc.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Embeddings.Local;

/// <summary>
/// One hit returned by <see cref="IEmbeddingIndex.SearchAsync"/>. Index is
/// the insertion-order id assigned by <see cref="IEmbeddingIndex.AddAsync"/>.
/// Higher <see cref="Score"/> = closer.
/// </summary>
public readonly record struct EmbeddingIndexHit(long InternalId, float Score);

/// <summary>
/// (RT-09b) Vector index contract. The store layers documents + metadata
/// + persistence on top; the index is the search primitive.
/// </summary>
public interface IEmbeddingIndex : IDisposable
{
    /// <summary>Vector dimensionality. Locked at construction.</summary>
    int Dimension { get; }

    /// <summary>How many vectors are currently in the index.</summary>
    long Count { get; }

    /// <summary>
    /// Append one vector. Returns the internal id the index assigned —
    /// callers map it back to a document id.
    /// </summary>
    ValueTask<long> AddAsync(ReadOnlyMemory<float> vector, CancellationToken ct = default);

    /// <summary>
    /// Search for the top-<paramref name="topK"/> nearest neighbours.
    /// </summary>
    ValueTask<EmbeddingIndexHit[]> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int                   topK,
        CancellationToken     ct = default);

    /// <summary>
    /// Persist the index to <paramref name="path"/>.
    /// </summary>
    ValueTask SaveAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Reload from <paramref name="path"/>, replacing the in-memory state.
    /// </summary>
    ValueTask LoadAsync(string path, CancellationToken ct = default);
}
