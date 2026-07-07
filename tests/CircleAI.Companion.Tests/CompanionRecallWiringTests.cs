// CompanionRecallWiringTests.cs
//
// (M1) Proves the loop routes recall through IRecall when one is wired, and
// falls back to flat episodic recall when it is not — i.e. the injection is the
// fused-vs-flat switch, with no behaviour change when off.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Domain;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Companion.Tests;

public class CompanionRecallWiringTests
{
    private sealed class FakeRecall : IRecall
    {
        private readonly IReadOnlyList<MemoryHit> _hits;
        public FakeRecall(params string[] texts) =>
            _hits = texts.Select(t => new MemoryHit(new MemoryItem(Guid.NewGuid().ToString(), t), 1f)).ToList();

        public Task<IReadOnlyList<MemoryHit>> RecallAsync(
            string query, float[]? queryEmbedding, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult(_hits);
    }

    private sealed class RecencyEpisodic : IEpisodicMemoryStore
    {
        private readonly List<EpisodicMemoryEntry> _entries;
        public RecencyEpisodic(params string[] userTexts) =>
            _entries = userTexts.Select(t => new EpisodicMemoryEntry { UserText = t }).ToList();

        public Task AddAsync(EpisodicMemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<EpisodicMemoryEntry>> SearchAsync(float[]? q, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EpisodicMemoryEntry>>(_entries.Take(topK).ToList());
        public Task<IReadOnlyList<EpisodicMemoryEntry>> GetRecentAsync(int count = 10, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EpisodicMemoryEntry>>(_entries.Take(count).ToList());
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_entries.Count);
        public Task<int> PruneOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task WithRecall_ContextComesFromFusedRecall()
    {
        var session = new CompanionSession(
            "user-1", "User One", default, preferredLanguage: null,
            recall: new FakeRecall("fused-remembered-fact"));

        await session.RefreshContextAsync();

        Assert.Contains(session.GetContext().RecentMemorySnippets, s => s.Contains("fused-remembered-fact"));
    }

    [Fact]
    public async Task WithoutRecall_FallsBackToEpisodic()
    {
        var session = new CompanionSession(
            "user-1", "User One", default, preferredLanguage: null,
            episodic: new RecencyEpisodic("flat-episodic-fact"));

        await session.RefreshContextAsync();

        Assert.Contains(session.GetContext().RecentMemorySnippets, s => s.Contains("flat-episodic-fact"));
    }

    [Fact]
    public async Task SelfFacts_ReachThePrompt_ThirdPartyDoesNot()
    {
        var beliefs = new SelfBeliefStore();
        beliefs.Record(new PersonalBelief(Attribution.Self, "user", "diet", "vegetarian", 0.9f, "t1", System.DateTimeOffset.UtcNow));
        beliefs.Record(new PersonalBelief(Attribution.Other, "mother", "condition", "diabetic", 0.9f, "t2", System.DateTimeOffset.UtcNow));

        var session = new CompanionSession(
            "user-1", "User One", default, preferredLanguage: null, beliefs: beliefs);
        await session.RefreshContextAsync();

        var facts = session.GetContext().UserFacts;
        Assert.Contains(facts, f => f.Contains("vegetarian"));
        Assert.DoesNotContain(facts, f => f.Contains("diabetic"));
    }
}
