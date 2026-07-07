// FusedRecallTests.cs
//
// (M1) Proves the Reciprocal-Rank-Fusion recall: cold-start parity, cross-source
// reinforcement, dedup, the confidence gate, and the input guards. Uses fakes so
// the fusion logic is tested in isolation from real stores.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Domain;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Companion.Tests;

public class FusedRecallTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeEpisodic : IEpisodicMemoryStore
    {
        private readonly List<EpisodicMemoryEntry> _ranked;
        public FakeEpisodic(params string[] userTexts) =>
            _ranked = userTexts.Select(t => new EpisodicMemoryEntry { UserText = t }).ToList();

        public Task AddAsync(EpisodicMemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<EpisodicMemoryEntry>> SearchAsync(
            float[]? queryEmbedding, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EpisodicMemoryEntry>>(_ranked.Take(topK).ToList());

        public Task<IReadOnlyList<EpisodicMemoryEntry>> GetRecentAsync(int count = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EpisodicMemoryEntry>>(_ranked.Take(count).ToList());

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_ranked.Count);

        public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeGraph : IHippoRagStore
    {
        private readonly List<MemoryHit> _ranked;
        public FakeGraph(params MemoryHit[] hits) => _ranked = hits.ToList();

        public string BackendId => "fake-graph";
        public ValueTask IndexAsync(MemoryItem item, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<MemoryHit>> MultiHopRecallAsync(string query, int topK = 5, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<MemoryHit>>(_ranked.Take(topK).ToList());
    }

    private static MemoryHit GraphHit(string text, float score = 1f, string? confidence = null)
    {
        Dictionary<string, string>? meta = confidence is null
            ? null
            : new Dictionary<string, string> { ["confidence"] = confidence };
        return new MemoryHit(new MemoryItem(Guid.NewGuid().ToString(), text, meta), score);
    }

    private static List<string> Texts(IReadOnlyList<MemoryHit> hits) => hits.Select(h => h.Item.Text).ToList();

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ColdStart_NoGraph_PreservesEpisodicOrder()
    {
        var recall = new FusedRecall(new FakeEpisodic("A", "B", "C"), graph: null);
        var hits = await recall.RecallAsync("q", queryEmbedding: null, topK: 3);
        Assert.Equal(new[] { "A", "B", "C" }, Texts(hits));
    }

    [Fact]
    public async Task EmptyGraph_DegradesToEpisodic()
    {
        var recall = new FusedRecall(new FakeEpisodic("A", "B", "C"), new FakeGraph());
        var hits = await recall.RecallAsync("q", null, 3);
        Assert.Equal(new[] { "A", "B", "C" }, Texts(hits));
    }

    [Fact]
    public async Task ReinforcedAcrossBothSources_RanksHighest()
    {
        // "C" is last in episodic but first in graph → RRF should lift it to the top,
        // and it must appear once, not twice.
        var recall = new FusedRecall(
            new FakeEpisodic("A", "B", "C"),
            new FakeGraph(GraphHit("C"), GraphHit("D")));
        var hits = await recall.RecallAsync("q", null, 4);

        Assert.Equal("C", hits[0].Item.Text);
        Assert.Equal(1, Texts(hits).Count(t => t == "C"));
    }

    [Fact]
    public async Task ConfidenceGate_DropsLowConfidenceGraphHits()
    {
        var recall = new FusedRecall(
            new FakeEpisodic("A"),
            new FakeGraph(GraphHit("low", confidence: "0.1"), GraphHit("high", confidence: "0.9")));
        var hits = await recall.RecallAsync("q", null, 5);
        var texts = Texts(hits);

        Assert.Contains("high", texts);
        Assert.DoesNotContain("low", texts);
    }

    [Fact]
    public async Task EmptyQuery_SkipsGraph_UsesEpisodic()
    {
        var recall = new FusedRecall(new FakeEpisodic("A"), new FakeGraph(GraphHit("G")));
        var hits = await recall.RecallAsync("   ", null, 5);
        var texts = Texts(hits);

        Assert.DoesNotContain("G", texts);
        Assert.Contains("A", texts);
    }

    [Fact]
    public async Task TopK_IsRespected()
    {
        var recall = new FusedRecall(new FakeEpisodic("A", "B", "C", "D", "E"));
        var hits = await recall.RecallAsync("q", null, 2);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task InvalidTopK_Throws()
    {
        var recall = new FusedRecall(new FakeEpisodic("A"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => recall.RecallAsync("q", null, 0));
    }

    [Fact]
    public void NullEpisodic_Throws() => Assert.Throws<ArgumentNullException>(() => new FusedRecall(null!));
}
