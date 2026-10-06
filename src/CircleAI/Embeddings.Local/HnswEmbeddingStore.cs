// HnswEmbeddingStore.cs
//
// (RT-09b) ICircleEmbeddingStore backed by the turbovec SIMD-blocked
// quantised search index. Same public contract as InMemoryEmbeddingStore;
// the only difference is the search path is O(N / SIMD-block) instead of
// O(N · D).
//
// On-disk format: a sidecar `.docs` file holds the id-to-document map
// (ordinal-keyed BinaryWriter); the turbovec index sits next to it as
// `<path>` itself. Save writes both atomically; Load reads both.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Embeddings.Local;

/// <summary>
/// (RT-09b) Embedding store backed by a <see cref="TurboVecEmbeddingIndex"/>.
/// Vectors are quantised to 4 bits/dim by default; search is SIMD-blocked.
/// </summary>
public sealed class HnswEmbeddingStore : ICircleEmbeddingStore
{
    private const int    DocsMagic       = 0x53434847; // "HGCS" — Hnsw Generic Circle Store
    private const ushort DocsVersion     = 1;
    private const int    DefaultBitWidth = 4;

    private readonly IEmbeddingEncoder _encoder;
    private readonly TurboVecEmbeddingIndex _index;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Ordinal internal-id -> EmbeddingDocument. Index aligns with turbovec slot ids.</summary>
    private readonly List<EmbeddingDocument> _byId = new();

    /// <summary>External-document-id -> internal-id. For O(1) Remove.</summary>
    private readonly ConcurrentDictionary<string, long> _idLookup = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <inheritdoc/>
    public int Dimension => _encoder.Dimension;

    /// <inheritdoc/>
    public int Count => _byId.Count;

    /// <summary>
    /// Construct with an encoder. The encoder's <see cref="IEmbeddingEncoder.Dimension"/>
    /// must be > 0 and a multiple of 8 (turbovec SIMD alignment).
    /// <paramref name="bitWidth"/> must be 2, 3, or 4.
    /// </summary>
    public HnswEmbeddingStore(IEmbeddingEncoder encoder, int bitWidth = DefaultBitWidth)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        if (_encoder.Dimension <= 0 || _encoder.Dimension % 8 != 0)
            throw new ArgumentException(
                $"Encoder dimension {_encoder.Dimension} must be > 0 and a multiple of 8 for turbovec.",
                nameof(encoder));
        _index = new TurboVecEmbeddingIndex(_encoder.Dimension, bitWidth);
    }

    /// <inheritdoc/>
    public async ValueTask AddAsync(EmbeddingDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var vector = await _encoder.EncodeAsync(document.Text, ct).ConfigureAwait(false);
        await AddAsync(document, vector, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask AddAsync(
        EmbeddingDocument document,
        ReadOnlyMemory<float> vector,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDisposed();
        if (vector.Length != Dimension)
            throw new ArgumentException(
                $"Vector length {vector.Length} != store dimension {Dimension}.",
                nameof(vector));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Replace-by-id is not supported by turbovec yet; v1 contract is
            // "add only." Callers that need replace should Remove first.
            if (_idLookup.ContainsKey(document.Id))
                throw new InvalidOperationException(
                    $"Document id '{document.Id}' already exists. Call RemoveAsync first.");

            var internalId = await _index.AddAsync(vector, ct).ConfigureAwait(false);
            _byId.Add(document);
            _idLookup[document.Id] = internalId;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();
        // turbovec's swap_remove is supported on IdMapIndex but our wrapped
        // TurboQuantIndex does not expose it through the C ABI yet. v1
        // semantics: mark deleted in the lookup so subsequent searches
        // skip the slot. Compaction is a 2.1.1 follow-up.
        if (_idLookup.TryRemove(id, out _))
            return ValueTask.FromResult(true);
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EmbeddingSearchHit>> SearchAsync(
        string queryText, int topK = 5, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryText);
        var vector = await _encoder.EncodeAsync(queryText, ct).ConfigureAwait(false);
        return await SearchAsync(vector, topK, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EmbeddingSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector, int topK = 5, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (queryVector.Length != Dimension)
            throw new ArgumentException(
                $"Query length {queryVector.Length} != store dimension {Dimension}.",
                nameof(queryVector));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        // Over-fetch to compensate for removed slots; cap to current count.
        var overFetch = Math.Min((int)_index.Count, Math.Max(topK * 2, topK + 10));
        if (overFetch == 0) return Array.Empty<EmbeddingSearchHit>();

        var rawHits = await _index.SearchAsync(queryVector, overFetch, ct).ConfigureAwait(false);
        if (rawHits.Length == 0) return Array.Empty<EmbeddingSearchHit>();

        var results = new List<EmbeddingSearchHit>(topK);
        foreach (var hit in rawHits)
        {
            if (hit.InternalId < 0 || hit.InternalId >= _byId.Count) continue;
            var doc = _byId[(int)hit.InternalId];
            if (!_idLookup.ContainsKey(doc.Id)) continue; // removed
            results.Add(new EmbeddingSearchHit(doc, hit.Score));
            if (results.Count == topK) break;
        }
        return results;
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Persist turbovec slot data through the bridge.
            await _index.SaveAsync(path, ct).ConfigureAwait(false);

            // Persist the doc sidecar.
            var docsPath = path + ".docs";
            var tmp = docsPath + ".tmp";
            await using (var fs = File.Create(tmp))
            await using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                bw.Write(DocsMagic);
                bw.Write(DocsVersion);
                bw.Write(Dimension);
                bw.Write(_byId.Count);
                for (var i = 0; i < _byId.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var doc = _byId[i];
                    bw.Write(doc.Id);
                    bw.Write(doc.Text);
                    bw.Write(_idLookup.ContainsKey(doc.Id)); // live flag
                    bw.Write(doc.Metadata?.Count ?? 0);
                    if (doc.Metadata is not null)
                    {
                        foreach (var (k, v) in doc.Metadata)
                        {
                            bw.Write(k);
                            bw.Write(v);
                        }
                    }
                }
            }
            if (File.Exists(docsPath)) File.Delete(docsPath);
            File.Move(tmp, docsPath);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async ValueTask LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        var docsPath = path + ".docs";
        if (!File.Exists(path))
            throw new FileNotFoundException("Index file not found.", path);
        if (!File.Exists(docsPath))
            throw new FileNotFoundException("Docs sidecar not found.", docsPath);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _index.LoadAsync(path, ct).ConfigureAwait(false);

            await using var fs = File.OpenRead(docsPath);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            var magic = br.ReadInt32();
            if (magic != DocsMagic) throw new InvalidDataException("Not an HnswEmbeddingStore docs sidecar.");
            var version = br.ReadUInt16();
            if (version != DocsVersion) throw new InvalidDataException($"Unsupported docs version {version}.");
            var fileDim = br.ReadInt32();
            if (fileDim != Dimension)
                throw new InvalidDataException($"Dimension mismatch: store={Dimension}, file={fileDim}.");
            var count = br.ReadInt32();

            _byId.Clear();
            _idLookup.Clear();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var id   = br.ReadString();
                var text = br.ReadString();
                var live = br.ReadBoolean();
                var metaCount = br.ReadInt32();
                Dictionary<string, string>? metadata = null;
                if (metaCount > 0)
                {
                    metadata = new Dictionary<string, string>(metaCount);
                    for (var m = 0; m < metaCount; m++)
                        metadata[br.ReadString()] = br.ReadString();
                }
                var doc = new EmbeddingDocument(id, text, metadata);
                _byId.Add(doc);
                if (live) _idLookup[id] = i;
            }
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _index.Dispose();
        _byId.Clear();
        _idLookup.Clear();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HnswEmbeddingStore));
    }
}
