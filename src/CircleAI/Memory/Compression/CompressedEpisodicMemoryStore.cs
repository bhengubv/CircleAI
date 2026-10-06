// CompressedEpisodicMemoryStore.cs
//
// Decorator over IEpisodicMemoryStore that transparently TurboQuant-
// compresses embeddings on write and reconstructs them on read.
//
// Storage layout:
//   • Entry written to inner store has Embedding = null (so the inner
//     store doesn't duplicate the data)
//   • Tags get an extra key "x-tq-embedding" whose value is the
//     base64-encoded TurboQuant payload
//
// Reads materialise the original Embedding by decoding the tag.
// SearchAsync rebuilds embeddings on the read path so cosine ranking
// works against the reconstructed vectors — same accuracy bound as the
// codec.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Compression;

/// <summary>
/// Wraps any <see cref="IEpisodicMemoryStore"/> and stores its embeddings
/// in TurboQuant-compressed form. Default 2 bits per dim (~16× shrink).
/// </summary>
public sealed class CompressedEpisodicMemoryStore : IEpisodicMemoryStore
{
    /// <summary>Tag key under which the compressed embedding is stored.</summary>
    public const string CompressedTagKey = "x-tq-embedding";

    private readonly IEpisodicMemoryStore _inner;
    private readonly int _bitsPerDim;

    public CompressedEpisodicMemoryStore(IEpisodicMemoryStore inner, int bitsPerDim = 2)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (bitsPerDim is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDim), "bitsPerDim must be 1..8");
        _inner = inner;
        _bitsPerDim = bitsPerDim;
    }

    public Task AddAsync(EpisodicMemoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var rewritten = entry.Embedding is { Length: > 1 }
            ? new EpisodicMemoryEntry
            {
                Id = entry.Id,
                RecordedAtUtc = entry.RecordedAtUtc,
                UserText = entry.UserText,
                AssistantText = entry.AssistantText,
                AppContext = entry.AppContext,
                Embedding = null, // dropped — lives in tags
                Tags = CopyTagsWithCompressed(entry.Tags, entry.Embedding),
            }
            : entry;
        return _inner.AddAsync(rewritten, ct);
    }

    public async Task<IReadOnlyList<EpisodicMemoryEntry>> SearchAsync(
        float[]? queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        // We CANNOT defer to the inner SearchAsync's cosine ranking because
        // the inner store sees Embedding = null on every entry. Instead we
        // load recent entries via inner.GetRecentAsync, rehydrate, then rank.
        var all = await _inner.GetRecentAsync(int.MaxValue, ct).ConfigureAwait(false);
        var rehydrated = all.Select(Rehydrate).ToList();

        if (queryEmbedding is null)
            return rehydrated.Take(topK).ToList();

        return rehydrated
            .Where(e => e.Embedding is { Length: > 0 })
            .Select(e => (entry: e, score: CosineSimilarity.Score(queryEmbedding, e.Embedding!)))
            .OrderByDescending(t => t.score)
            .Take(topK)
            .Select(t => t.entry)
            .ToList();
    }

    public async Task<IReadOnlyList<EpisodicMemoryEntry>> GetRecentAsync(
        int count = 10, CancellationToken ct = default)
    {
        var recent = await _inner.GetRecentAsync(count, ct).ConfigureAwait(false);
        return recent.Select(Rehydrate).ToList();
    }

    public Task<int> CountAsync(CancellationToken ct = default) => _inner.CountAsync(ct);

    public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => _inner.PruneOlderThanAsync(cutoff, ct);

    // ── Helpers ──────────────────────────────────────────────────────────

    private Dictionary<string, string> CopyTagsWithCompressed(
        Dictionary<string, string>? src, float[] embedding)
    {
        var dict = src is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(src, StringComparer.OrdinalIgnoreCase);
        dict[CompressedTagKey] = EmbeddingPayloadCodec.EncodeBase64(embedding, _bitsPerDim);
        return dict;
    }

    private static EpisodicMemoryEntry Rehydrate(EpisodicMemoryEntry e)
    {
        if (e.Embedding is { Length: > 0 }) return e; // never compressed
        if (e.Tags is null || !e.Tags.TryGetValue(CompressedTagKey, out var b64)) return e;
        try
        {
            var floats = EmbeddingPayloadCodec.DecodeBase64(b64);
            return new EpisodicMemoryEntry
            {
                Id = e.Id,
                RecordedAtUtc = e.RecordedAtUtc,
                UserText = e.UserText,
                AssistantText = e.AssistantText,
                AppContext = e.AppContext,
                Embedding = floats,
                Tags = e.Tags,
            };
        }
        catch
        {
            // Malformed tag — return entry as-is so the caller can still see it.
            return e;
        }
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
