// CompressedMultimodalMemoryStore.cs
//
// Decorator over IMultimodalMemoryStore that TurboQuant-compresses
// embeddings the same way CompressedEpisodicMemoryStore does for episodic.
// The wire format and tag key are the same (x-tq-embedding) so a single
// audit pass can verify both surfaces.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Memory.Multimodal;

namespace CircleAI.Memory.Compression;

/// <summary>
/// Wraps any <see cref="IMultimodalMemoryStore"/> and stores its embeddings
/// in TurboQuant-compressed form.
/// </summary>
public sealed class CompressedMultimodalMemoryStore : IMultimodalMemoryStore
{
    /// <summary>Tag key under which the compressed embedding is stored.</summary>
    public const string CompressedTagKey = CompressedEpisodicMemoryStore.CompressedTagKey;

    private readonly IMultimodalMemoryStore _inner;
    private readonly int _bitsPerDim;

    public CompressedMultimodalMemoryStore(IMultimodalMemoryStore inner, int bitsPerDim = 2)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (bitsPerDim is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDim), "bitsPerDim must be 1..8");
        _inner = inner;
        _bitsPerDim = bitsPerDim;
    }

    public Task AddAsync(MultimodalMemoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var rewritten = entry.Embedding is { Length: > 1 } ? Compress(entry) : entry;
        return _inner.AddAsync(rewritten, ct);
    }

    public async Task<MultimodalMemoryEntry?> GetByHashAsync(string sourceSha256, CancellationToken ct = default)
    {
        var got = await _inner.GetByHashAsync(sourceSha256, ct).ConfigureAwait(false);
        return got is null ? null : Rehydrate(got);
    }

    public Task ReinforceAsync(string sourceSha256, CancellationToken ct = default)
        => _inner.ReinforceAsync(sourceSha256, ct);

    public async Task<IReadOnlyList<MultimodalMemoryEntry>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        var all = await _inner.GetRecentAsync(int.MaxValue, ct).ConfigureAwait(false);
        var rehydrated = all.Select(Rehydrate).ToList();
        if (queryEmbedding is null) return rehydrated.Take(topK).ToList();

        return rehydrated
            .Where(e => e.Embedding is { Length: > 0 })
            .Select(e => (entry: e, score: CosineSimilarity.Score(queryEmbedding, e.Embedding!)))
            .OrderByDescending(t => t.score)
            .Take(topK)
            .Select(t => t.entry)
            .ToList();
    }

    public async Task<IReadOnlyList<MultimodalMemoryEntry>> GetRecentAsync(
        int count = 10, CancellationToken ct = default)
    {
        var recent = await _inner.GetRecentAsync(count, ct).ConfigureAwait(false);
        return recent.Select(Rehydrate).ToList();
    }

    public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => _inner.PruneOlderThanAsync(cutoff, ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _inner.CountAsync(ct);

    // ── Helpers ──────────────────────────────────────────────────────────

    private MultimodalMemoryEntry Compress(MultimodalMemoryEntry entry)
    {
        var tags = entry.Tags is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(entry.Tags, StringComparer.OrdinalIgnoreCase);
        tags[CompressedTagKey] = EmbeddingPayloadCodec.EncodeBase64(entry.Embedding!, _bitsPerDim);

        return new MultimodalMemoryEntry
        {
            Id = entry.Id,
            RecordedAtUtc = entry.RecordedAtUtc,
            Modality = entry.Modality,
            Caption = entry.Caption,
            Embedding = null,
            SourceSha256 = entry.SourceSha256,
            SourceMimeType = entry.SourceMimeType,
            SourceByteCount = entry.SourceByteCount,
            SourceUri = entry.SourceUri,
            WidthPx = entry.WidthPx,
            HeightPx = entry.HeightPx,
            DurationMs = entry.DurationMs,
            ReferenceCount = entry.ReferenceCount,
            Tags = tags,
        };
    }

    private static MultimodalMemoryEntry Rehydrate(MultimodalMemoryEntry e)
    {
        if (e.Embedding is { Length: > 0 }) return e;
        if (e.Tags is null || !e.Tags.TryGetValue(CompressedTagKey, out var b64)) return e;
        try
        {
            var floats = EmbeddingPayloadCodec.DecodeBase64(b64);
            return new MultimodalMemoryEntry
            {
                Id = e.Id,
                RecordedAtUtc = e.RecordedAtUtc,
                Modality = e.Modality,
                Caption = e.Caption,
                Embedding = floats,
                SourceSha256 = e.SourceSha256,
                SourceMimeType = e.SourceMimeType,
                SourceByteCount = e.SourceByteCount,
                SourceUri = e.SourceUri,
                WidthPx = e.WidthPx,
                HeightPx = e.HeightPx,
                DurationMs = e.DurationMs,
                ReferenceCount = e.ReferenceCount,
                Tags = e.Tags,
            };
        }
        catch { return e; }
    }

    private static class CosineSimilarity
    {
        public static float Score(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            double dot = 0, magA = 0, magB = 0;
            for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
            var d = Math.Sqrt(magA) * Math.Sqrt(magB);
            return d < double.Epsilon ? 0f : (float)(dot / d);
        }
    }
}
