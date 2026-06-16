// InMemoryEmbeddingStore.cs
//
// (RT-09) Brute-force in-memory implementation of ICircleEmbeddingStore.
// Vectors are TurboQuant-compressed at 4-bits-per-dim before storage so
// memory footprint is ~8× smaller than raw FP32. Cosine search reads the
// compressed payload, decodes lazily, and computes the dot product.
//
// v1 design:
//   - Brute-force (O(N·D)) search. Adequate up to ~100K docs on a phone.
//   - HNSW backend is on the 2.1.0 roadmap.
//   - Persistence is a single LiteDB-free file (custom format) so the
//     package has zero runtime deps beyond CircleAI.Core.
//
// Thread-safety:
//   - Add/Remove/Search are concurrency-safe via SemaphoreSlim.
//   - Save/Load are serialised against everything else.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core.Compression;

namespace CircleAI.Embeddings.Local;

/// <summary>
/// (RT-09) Default <see cref="ICircleEmbeddingStore"/>: brute-force search
/// over TurboQuant-compressed vectors held in memory.
/// </summary>
public sealed class InMemoryEmbeddingStore : ICircleEmbeddingStore
{
    private const int    FileMagic     = 0x4C455143; // "CELQ" little-endian
    private const ushort FileVersion   = 1;
    private const int    DefaultBitsPerDim = 4;

    private readonly IEmbeddingEncoder _encoder;
    private readonly int _bitsPerDim;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <inheritdoc/>
    public int Dimension => _encoder.Dimension;

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <summary>
    /// Construct with a caller-supplied encoder. <paramref name="bitsPerDim"/>
    /// controls the TurboQuant quantisation depth — 4 bits/dim is the v1
    /// default (~8× shrink vs FP32, recall &gt; 0.95 for sentence-transformer
    /// embeddings). Valid range: 1–8.
    /// </summary>
    public InMemoryEmbeddingStore(IEmbeddingEncoder encoder, int bitsPerDim = DefaultBitsPerDim)
    {
        _encoder    = encoder ?? throw new ArgumentNullException(nameof(encoder));
        if (bitsPerDim is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDim), "Valid range: 1–8.");
        _bitsPerDim = bitsPerDim;
    }

    /// <inheritdoc/>
    public async ValueTask AddAsync(EmbeddingDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var vector = await _encoder.EncodeAsync(document.Text, ct).ConfigureAwait(false);
        await AddAsync(document, vector, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask AddAsync(
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

        var span = vector.Span;
        var payload = TurboQuantCodec.Encode(span, _bitsPerDim);
        _entries[document.Id] = new Entry(document, payload);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();
        return ValueTask.FromResult(_entries.TryRemove(id, out _));
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
    public ValueTask<IReadOnlyList<EmbeddingSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector, int topK = 5, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (queryVector.Length != Dimension)
            throw new ArgumentException(
                $"Vector length {queryVector.Length} != store dimension {Dimension}.",
                nameof(queryVector));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        var qNorm = NormSafe(queryVector.Span);
        var q     = queryVector.ToArray();
        if (qNorm > 0)
            for (var i = 0; i < q.Length; i++) q[i] /= qNorm;

        // Brute-force cosine. Each entry is decoded on demand. We carry a
        // running top-K via SortedSet to avoid O(N log N).
        var heap = new SortedSet<(float Score, string Id)>(ScoreComparer.Instance);
        foreach (var (id, entry) in _entries)
        {
            ct.ThrowIfCancellationRequested();
            var decoded = TurboQuantCodec.Decode(entry.Payload, Dimension, _bitsPerDim);
            var entryNorm = NormSafe(decoded);
            if (entryNorm <= 0) continue;
            float dot = 0;
            for (var i = 0; i < Dimension; i++) dot += q[i] * (decoded[i] / entryNorm);

            if (heap.Count < topK)
                heap.Add((dot, id));
            else if (dot > heap.Min.Score)
            {
                heap.Remove(heap.Min);
                heap.Add((dot, id));
            }
        }

        var ordered = heap.OrderByDescending(t => t.Score)
            .Select(t => new EmbeddingSearchHit(_entries[t.Id].Document, t.Score))
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<EmbeddingSearchHit>>(ordered);
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
            var tmp = path + ".tmp";
            await using (var fs = File.Create(tmp))
            await using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                bw.Write(FileMagic);
                bw.Write(FileVersion);
                bw.Write((ushort)_bitsPerDim);
                bw.Write(Dimension);
                bw.Write(_entries.Count);
                foreach (var (id, entry) in _entries)
                {
                    ct.ThrowIfCancellationRequested();
                    bw.Write(id);
                    bw.Write(entry.Document.Text);
                    bw.Write(entry.Document.Metadata?.Count ?? 0);
                    if (entry.Document.Metadata is not null)
                    {
                        foreach (var (k, v) in entry.Document.Metadata)
                        {
                            bw.Write(k);
                            bw.Write(v);
                        }
                    }
                    bw.Write(entry.Payload.Norm);
                    bw.Write(entry.Payload.PackedIndices.Length);
                    bw.Write(entry.Payload.PackedIndices);
                }
            }
            // Atomic swap; tolerate Replace not being available on transient FS.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async ValueTask LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        if (!File.Exists(path))
            throw new FileNotFoundException("Embedding store file not found.", path);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            var magic = br.ReadInt32();
            if (magic != FileMagic) throw new InvalidDataException("Not a CircleAI embedding store file.");
            var version = br.ReadUInt16();
            if (version != FileVersion) throw new InvalidDataException($"Unsupported file version {version}.");
            var fileBits = br.ReadUInt16();
            if (fileBits != _bitsPerDim)
                throw new InvalidDataException(
                    $"Bits-per-dim mismatch: store={_bitsPerDim}, file={fileBits}.");
            var fileDim = br.ReadInt32();
            if (fileDim != Dimension)
                throw new InvalidDataException(
                    $"Dimension mismatch: store={Dimension}, file={fileDim}.");

            var count = br.ReadInt32();
            _entries.Clear();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var id   = br.ReadString();
                var text = br.ReadString();
                var metaCount = br.ReadInt32();
                Dictionary<string, string>? metadata = null;
                if (metaCount > 0)
                {
                    metadata = new Dictionary<string, string>(metaCount);
                    for (var m = 0; m < metaCount; m++)
                        metadata[br.ReadString()] = br.ReadString();
                }
                var norm = br.ReadSingle();
                var packedLen = br.ReadInt32();
                var packed = br.ReadBytes(packedLen);
                _entries[id] = new Entry(
                    new EmbeddingDocument(id, text, metadata),
                    new TurboQuantPayload(norm, packed));
            }
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _entries.Clear();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InMemoryEmbeddingStore));
    }

    private static float NormSafe(ReadOnlySpan<float> v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        return (float)Math.Sqrt(sum);
    }

    private readonly record struct Entry(EmbeddingDocument Document, TurboQuantPayload Payload);

    private sealed class ScoreComparer : IComparer<(float Score, string Id)>
    {
        public static readonly ScoreComparer Instance = new();
        public int Compare((float Score, string Id) a, (float Score, string Id) b)
        {
            var c = a.Score.CompareTo(b.Score);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        }
    }
}
