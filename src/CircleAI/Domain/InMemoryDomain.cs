// InMemoryDomain.cs
//
// (3.3.0) Real-but-lightweight in-memory implementations for every
// CircleAI.Domain contract surface. None of these are LLM-grade
// experts — they are the deterministic in-process backings tests
// expect, and the fallbacks production hosts swap out one-by-one as
// real specialists (EPICure / quant-mind / dexter / presenton /
// career-ops / mempalace / HippoRAG / MiroFish) get vendored.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Domain;

// ─── Food (substitute-by-canonical-name) ───────────────────────────────
public sealed class InMemoryFoodEmbeddings : IFoodEmbeddings
{
    private readonly ConcurrentDictionary<string, float[]> _embeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<Ingredient>> _subs = new(StringComparer.OrdinalIgnoreCase);

    public string BackendId => "in-memory";

    public void RegisterEmbedding(string name, float[] v) => _embeds[name] = v ?? throw new ArgumentNullException(nameof(v));
    public void RegisterSubstitute(string name, Ingredient alt)
    {
        var list = _subs.GetOrAdd(name, _ => new List<Ingredient>());
        list.Add(alt ?? throw new ArgumentNullException(nameof(alt)));
    }

    public ValueTask<float[]> EmbedAsync(Ingredient i, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(i);
        if (_embeds.TryGetValue(i.Name, out var v)) return ValueTask.FromResult(v);
        // Deterministic hash-based 8-dim vector if no embedding was registered.
        var v2 = new float[8];
        var h = i.Name.GetHashCode(StringComparison.OrdinalIgnoreCase);
        for (var k = 0; k < 8; k++) v2[k] = ((h >> (k * 4)) & 0xF) / 15f;
        return ValueTask.FromResult(v2);
    }

    public ValueTask<IReadOnlyList<Ingredient>> SubstitutesAsync(Ingredient i, int topK = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(i);
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        if (!_subs.TryGetValue(i.Name, out var list)) return ValueTask.FromResult<IReadOnlyList<Ingredient>>(Array.Empty<Ingredient>());
        return ValueTask.FromResult<IReadOnlyList<Ingredient>>(list.Take(topK).ToArray());
    }
}

// ─── Finance ───────────────────────────────────────────────────────────
public sealed class InMemoryFinanceRetrieval : IFinanceRetrieval
{
    private readonly List<FinanceSnippet> _corpus = new();
    private readonly object _lock = new();
    public string BackendId => "in-memory";

    public void Add(FinanceSnippet s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _corpus.Add(s); }

    public ValueTask<IReadOnlyList<FinanceSnippet>> RetrieveAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0)     throw new ArgumentOutOfRangeException(nameof(topK));
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<FinanceSnippet>>(
                _corpus.Where(s => s.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(s => s.Score)
                       .Take(topK).ToArray());
        }
    }
}

/// <summary>(3.3.0) Real financial agent — runs multi-pass retrieval (decomposes the
/// question into sub-questions), groups findings by source, summarises each cluster.</summary>
public sealed class MultiPassFinancialAgent : IFinancialAgent
{
    private readonly IFinanceRetrieval _retr;
    public MultiPassFinancialAgent(IFinanceRetrieval r) => _retr = r ?? throw new ArgumentNullException(nameof(r));
    public string BackendId => "multi-pass";

    public async ValueTask<IReadOnlyList<FinanceFinding>> ResearchAsync(string question, CancellationToken ct = default)
    {
        if (question is null) throw new ArgumentNullException(nameof(question));
        var subQuestions = Decompose(question);
        var findings = new List<FinanceFinding>();
        foreach (var sub in subQuestions)
        {
            var snippets = await _retr.RetrieveAsync(sub, 5, ct).ConfigureAwait(false);
            if (snippets.Count == 0) continue;
            var bySource = snippets.GroupBy(s => s.Source);
            foreach (var grp in bySource)
            {
                var summary = string.Join(" | ", grp.OrderByDescending(s => s.Score).Take(3).Select(s => s.Text));
                findings.Add(new FinanceFinding(Subject: sub, Summary: summary, Citations: new[] { grp.Key }));
            }
        }
        return findings.Count == 0 ? Array.Empty<FinanceFinding>() : findings;
    }

    private static IReadOnlyList<string> Decompose(string question)
    {
        var subs = new List<string> { question };
        if (question.Contains(" and ", StringComparison.OrdinalIgnoreCase))
            foreach (var part in question.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Trim().Length > 6) subs.Add(part.Trim());
        if (question.Length > 60)
        {
            subs.Add(question.Split(',').First().Trim());
        }
        return subs.Distinct().ToArray();
    }
}


// ─── Presentations ──────────────────────────────────────────────────────
public sealed class TemplatePresentationGenerator : IPresentationGenerator
{
    public string BackendId => "template";

    public ValueTask<GeneratedPresentation> GenerateAsync(string topic, int targetSlideCount = 10, string? theme = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))  throw new ArgumentException("topic required");
        if (targetSlideCount <= 0)             throw new ArgumentOutOfRangeException(nameof(targetSlideCount));
        var slides = new List<SlideOutline>(targetSlideCount);
        slides.Add(new SlideOutline(topic, "Overview", new[] { "What is " + topic, "Why it matters", "What we'll cover" }));
        for (var i = 2; i < targetSlideCount; i++)
        {
            slides.Add(new SlideOutline($"{topic} — Part {i - 1}", $"Detail for part {i - 1}", new[] { "Point A", "Point B", "Point C" }));
        }
        slides.Add(new SlideOutline("Conclusion", $"Summary of {topic}", new[] { "Recap", "Next steps", "Questions" }));
        return ValueTask.FromResult(new GeneratedPresentation(slides, theme ?? "default", "markdown"));
    }
}

// ─── Job search ─────────────────────────────────────────────────────────
public sealed class TemplateJobSearchPipeline : IJobSearchPipeline
{
    public string BackendId => "template";

    public ValueTask<JobApplicationDraft> DraftApplicationAsync(string roleDescription, string candidateProfileText, CancellationToken ct = default)
    {
        if (roleDescription is null)      throw new ArgumentNullException(nameof(roleDescription));
        if (candidateProfileText is null) throw new ArgumentNullException(nameof(candidateProfileText));
        var roleWords = ExtractKeyWords(roleDescription);
        var candWords = ExtractKeyWords(candidateProfileText);
        var matches = roleWords.Intersect(candWords, StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        var resume = $"{candidateProfileText.Trim()}\n\nMatched skills: {string.Join(", ", matches)}";
        var cover  = $"Dear Hiring Team,\n\nI am applying because my background ({string.Join(", ", matches.Take(3))}) fits the role.\n\nRegards.";
        return ValueTask.FromResult(new JobApplicationDraft(resume, cover, matches));
    }

    private static IReadOnlyList<string> ExtractKeyWords(string text)
        => text.Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
               .Where(w => w.Length > 3).Select(w => w.Trim().ToLowerInvariant()).Distinct().ToArray();
}

// ─── Memory upgrades ────────────────────────────────────────────────────
public sealed class InMemoryMemPalaceStore : IMemPalaceStore
{
    private readonly ConcurrentDictionary<string, MemoryItem> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask UpsertAsync(MemoryItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id)) throw new ArgumentException("Id required");
        _items[item.Id] = item;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<MemoryHit>> RecallAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0)     throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _items.Values
            .Select(i => new MemoryHit(i, Score(i.Text, query)))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<MemoryHit>>(hits);
    }

    private static float Score(string body, string query)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(query)) return 0f;
        var q = query.Trim();
        var idx = body.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? 0f : 1f / (1f + idx);
    }
}

public sealed class InMemoryHippoRagStore : IHippoRagStore
{
    private readonly InMemoryMemPalaceStore _base = new();
    public string BackendId => "in-memory";

    public ValueTask IndexAsync(MemoryItem item, CancellationToken ct = default) => _base.UpsertAsync(item, ct);

    public async ValueTask<IReadOnlyList<MemoryHit>> MultiHopRecallAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        // Multi-hop: first hop with the query, then expand using top hit's text as a follow-up query.
        var first = await _base.RecallAsync(query, topK, ct).ConfigureAwait(false);
        if (first.Count == 0) return first;
        var seed   = first[0].Item.Text;
        var second = await _base.RecallAsync(seed, topK, ct).ConfigureAwait(false);
        return first.Concat(second).GroupBy(h => h.Item.Id).Select(g => g.First())
                    .OrderByDescending(h => h.Score).Take(topK).ToArray();
    }
}

// ─── Swarm ──────────────────────────────────────────────────────────────
public sealed class InMemorySwarmCoordinator : ISwarmCoordinator
{
    private readonly ConcurrentDictionary<string, SwarmPeer> _peers = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public void Register(SwarmPeer p) { ArgumentNullException.ThrowIfNull(p); _peers[p.PeerId] = p; }

    public ValueTask<IReadOnlyList<SwarmPeer>> ListPeersAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SwarmPeer>>(_peers.Values.ToArray());

    public ValueTask<string?> ChooseDelegateAsync(string capability, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(capability)) throw new ArgumentException("capability required", nameof(capability));
        var pick = _peers.Values
            .Where(p => string.Equals(p.Capability, capability, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Health)
            .FirstOrDefault();
        return ValueTask.FromResult(pick?.PeerId);
    }
}

// ─── Personal LoRA — real in-memory adapter manager with a simulated training loop ─────
public sealed class InMemoryPersonalLoRA : IPersonalLoRA
{
    private readonly ConcurrentDictionary<string, LoRAAdapterState> _adapters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _loaded = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public ValueTask<LoRATrainingSummary> TrainAsync(string adapterId, IReadOnlyList<string> samples, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("adapterId required");
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("at least one sample required");

        // Simulated training loop: each sample contributes a step. Final loss
        // decreases logarithmically with sample count (a realistic baseline shape).
        var steps     = samples.Count;
        var totalChars = samples.Sum(s => s?.Length ?? 0);
        var finalLoss = (float)(1.0 / (1.0 + Math.Log(1 + steps)) + 1.0 / (1.0 + totalChars / 1000.0));
        var state = new LoRAAdapterState(adapterId, steps, finalLoss, DateTimeOffset.UtcNow);
        _adapters[adapterId] = state;
        return ValueTask.FromResult(new LoRATrainingSummary(adapterId, steps, finalLoss));
    }

    public ValueTask LoadAdapterAsync(string adapterId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("adapterId required");
        if (!_adapters.ContainsKey(adapterId))
            throw new InvalidOperationException($"Adapter '{adapterId}' not trained.");
        _loaded[adapterId] = 1;
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAdapterAsync(string adapterId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("adapterId required");
        _loaded.TryRemove(adapterId, out _);
        return ValueTask.CompletedTask;
    }

    public bool IsLoaded(string adapterId) => _loaded.ContainsKey(adapterId);
    public LoRAAdapterState? StateOf(string adapterId) => _adapters.GetValueOrDefault(adapterId);
}

public sealed record LoRAAdapterState(string AdapterId, int Steps, float FinalLoss, DateTimeOffset TrainedAtUtc);

