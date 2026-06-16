// ICircleEmbeddingStore.cs
//
// (RT-09) The CircleAI embedding-store contract. v1 ships InMemoryEmbeddingStore;
// future backends (LiteDB-backed, HNSW, AetherNet-replicated) plug into the
// same surface. Bring your own encoder via IEmbeddingEncoder.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Embeddings.Local;

/// <summary>
/// One document in the store. <see cref="Id"/> is caller-chosen and
/// uniquely identifies the document for delete / update.
/// </summary>
public sealed record EmbeddingDocument(
    string                              Id,
    string                              Text,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// One hit from <see cref="ICircleEmbeddingStore.SearchAsync"/>. Higher
/// <see cref="Score"/> = closer. Cosine similarity: 1.0 = identical,
/// -1.0 = opposite, 0.0 = orthogonal.
/// </summary>
public sealed record EmbeddingSearchHit(
    EmbeddingDocument Document,
    float             Score);

/// <summary>
/// Translates text into a dense vector. Bring your own — sentence-transformers
/// via ONNX, or a small MNN encoder, or a cloud API.
/// </summary>
public interface IEmbeddingEncoder
{
    /// <summary>Vector dimension this encoder produces. All vectors fed to
    /// the store from the same encoder must agree.</summary>
    int Dimension { get; }

    /// <summary>Encode one text into a dense vector.</summary>
    ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// (RT-09) On-device embedding store with built-in RAG primitive. Add
/// documents once, search by text or vector. Vectors are TurboQuant-
/// compressed at 4-bits-per-dim so the store fits ~8× more documents in
/// the same RAM/disk footprint as raw FP32.
/// </summary>
public interface ICircleEmbeddingStore : IAsyncDisposable
{
    /// <summary>Vector dimension this store was created with.</summary>
    int Dimension { get; }

    /// <summary>How many documents are currently in the store.</summary>
    int Count { get; }

    /// <summary>
    /// Add (or replace) one document. The encoder produces the vector; the
    /// store quantises and indexes it.
    /// </summary>
    ValueTask AddAsync(
        EmbeddingDocument document,
        CancellationToken ct = default);

    /// <summary>
    /// Add a document with a caller-supplied vector. Use when the encoder
    /// is external (cloud API, batch-encoded ahead of time). Vector length
    /// must equal <see cref="Dimension"/>.
    /// </summary>
    ValueTask AddAsync(
        EmbeddingDocument document,
        ReadOnlyMemory<float> vector,
        CancellationToken ct = default);

    /// <summary>
    /// Remove a document by id. Returns <c>true</c> if a document was
    /// removed, <c>false</c> if no document with that id existed.
    /// </summary>
    ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Search by text. The encoder produces a query vector, the store
    /// returns the <paramref name="topK"/> closest documents by cosine
    /// similarity. v1 is brute-force; HNSW upgrade in 2.1.0.
    /// </summary>
    ValueTask<IReadOnlyList<EmbeddingSearchHit>> SearchAsync(
        string            queryText,
        int               topK = 5,
        CancellationToken ct   = default);

    /// <summary>
    /// Search by a pre-computed query vector. Vector length must equal
    /// <see cref="Dimension"/>.
    /// </summary>
    ValueTask<IReadOnlyList<EmbeddingSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int                   topK = 5,
        CancellationToken     ct   = default);

    /// <summary>
    /// Persist the entire store to <paramref name="path"/>. Atomic via
    /// write-tmp-then-rename.
    /// </summary>
    ValueTask SaveAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Load a previously-saved store from <paramref name="path"/>.
    /// Replaces all in-memory state.
    /// </summary>
    ValueTask LoadAsync(string path, CancellationToken ct = default);
}
