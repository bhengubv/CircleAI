// FusedRecall.cs
//
// (M1) Fuses two memory systems with incomparable score spaces —
// episodic cosine similarity (IEpisodicMemoryStore) and graph association
// (IHippoRagStore / Personalised PageRank) — into one ranked context using
// Reciprocal Rank Fusion (RRF).
//
// RRF combines ranked lists by *position*, so it needs no shared score scale:
// episodic returns an ordered list (cosine ranks, scores not exposed), graph
// returns PageRank-scored hits; each source contributes 1 / (k + rank).
//
// Cold-start is automatic: a new user has an empty graph, so only episodic
// contributes and the fused order equals the episodic order — no special case.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Domain;
using CircleAI.Memory;

namespace CircleAI.Companion;

/// <summary>(M1) Tuning for <see cref="FusedRecall"/>.</summary>
public sealed class FusedRecallOptions
{
    /// <summary>Candidates pulled from each source before fusion. Default 20.</summary>
    public int CandidatePoolSize { get; init; } = 20;

    /// <summary>RRF damping constant k. Default 60 (the standard value).</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>
    /// Graph hits whose backing confidence (metadata key <c>"confidence"</c>) is below
    /// this are dropped. Applied only when a hit actually carries a confidence value, so
    /// it is a no-op until confidence-weighted recall lands (M2). Default 0.4.
    /// </summary>
    public float GraphConfidenceThreshold { get; init; } = 0.4f;
}

/// <summary>
/// (M1) Reciprocal-Rank-Fusion recall over episodic similarity + graph association.
/// </summary>
public sealed class FusedRecall : IRecall
{
    private readonly IEpisodicMemoryStore _episodic;
    private readonly IHippoRagStore? _graph;
    private readonly FusedRecallOptions _options;

    public FusedRecall(
        IEpisodicMemoryStore episodic,
        IHippoRagStore? graph = null,
        FusedRecallOptions? options = null)
    {
        _episodic = episodic ?? throw new ArgumentNullException(nameof(episodic));
        _graph = graph;
        _options = options ?? new FusedRecallOptions();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryHit>> RecallAsync(
        string query, float[]? queryEmbedding, int topK = 5, CancellationToken ct = default)
    {
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        var pool = _options.CandidatePoolSize;

        // Fast path: episodic similarity (or recency when the embedding is null).
        var episodic = await _episodic.SearchAsync(queryEmbedding, pool, ct).ConfigureAwait(false);

        // Slow path: graph association. Optional and best-effort — a missing, empty,
        // or failing graph must degrade to pure episodic, never throw or add latency-fatal
        // work. An empty query cannot seed a graph walk, so skip it.
        IReadOnlyList<MemoryHit> graph = Array.Empty<MemoryHit>();
        if (_graph is not null && !string.IsNullOrWhiteSpace(query))
        {
            try
            {
                graph = await _graph.MultiHopRecallAsync(query, pool, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FusedRecall] graph recall failed: {ex.Message}");
                graph = Array.Empty<MemoryHit>();
            }
        }

        // Reciprocal Rank Fusion: accumulate 1 / (k + rank) per candidate across both
        // ranked lists, keyed by normalised text so a memory surfaced by both sources
        // reinforces rather than duplicates.
        var k = _options.RrfK;
        var fused = new Dictionary<string, FusedCandidate>(StringComparer.Ordinal);

        void Accumulate(MemoryItem item, int oneBasedRank)
        {
            var key = NormaliseKey(item.Text);
            if (key.Length == 0) return;
            var contribution = 1.0 / (k + oneBasedRank);
            if (fused.TryGetValue(key, out var existing))
                existing.Score += contribution;
            else
                fused[key] = new FusedCandidate(item, contribution);
        }

        for (var i = 0; i < episodic.Count; i++)
            Accumulate(AdaptEpisodic(episodic[i]), i + 1);

        for (var i = 0; i < graph.Count; i++)
        {
            if (IsBelowConfidence(graph[i])) continue;
            Accumulate(graph[i].Item, i + 1);
        }

        return fused.Values
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .Select(c => new MemoryHit(c.Item, (float)c.Score))
            .ToList();
    }

    private bool IsBelowConfidence(MemoryHit hit)
    {
        if (hit.Item.Metadata is null) return false;
        if (!hit.Item.Metadata.TryGetValue("confidence", out var raw)) return false;
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)
               && c < _options.GraphConfidenceThreshold;
    }

    private static MemoryItem AdaptEpisodic(EpisodicMemoryEntry e)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "episodic",
            ["recordedAt"] = e.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(e.AssistantText)) meta["assistantText"] = e.AssistantText;
        if (!string.IsNullOrEmpty(e.AppContext)) meta["appContext"] = e.AppContext!;
        return new MemoryItem(e.Id.ToString(), e.UserText, meta);
    }

    private static string NormaliseKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var prevSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else { sb.Append(char.ToLowerInvariant(ch)); prevSpace = false; }
        }
        return sb.ToString();
    }

    private sealed class FusedCandidate
    {
        public FusedCandidate(MemoryItem item, double score)
        {
            Item = item;
            Score = score;
        }

        public MemoryItem Item { get; }
        public double Score { get; set; }
    }
}
