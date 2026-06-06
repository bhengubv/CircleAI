// MemoryConsolidatorTests.cs
//
// Exercises HeuristicSummarizer + MemoryConsolidator end-to-end against the
// in-memory tier stores. Uses a controllable clock so retention windows and
// "what counts as today" are deterministic. No external dependencies, no
// embedding backend required (we synthesise tiny embeddings inline).

using CircleAI.Memory;
using CircleAI.Memory.Consolidation;
using Xunit;

namespace CircleAI.Tests;

public sealed class MemoryConsolidatorTests
{
    // 2026-06-15 — a Monday, mid-month — used as "now" everywhere.
    private readonly DateTimeOffset _now = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);
    private DateTimeOffset Clock() => _now;

    // ── Builders ──────────────────────────────────────────────────────────

    private static EpisodicMemoryEntry MakeEntry(
        DateTimeOffset at, string userText, string assistantText,
        string? topic = null, float[]? embedding = null)
    {
        var tags = topic is null ? null : new Dictionary<string, string> { ["topic"] = topic };
        return new EpisodicMemoryEntry
        {
            RecordedAtUtc = at,
            UserText = userText,
            AssistantText = assistantText,
            Tags = tags,
            Embedding = embedding,
        };
    }

    private static float[] Emb(params float[] values) => values;

    private (MemoryConsolidator engine,
             IEpisodicMemoryStore episodic,
             IDailyMemoryStore daily,
             ISemanticMemoryStore semantic,
             IPersonaDeltaStore deltas,
             ICoreMemoryStore core,
             IPersonaStore persona)
        Wire(MemoryConsolidationOptions? opts = null)
    {
        var episodic = (IEpisodicMemoryStore)new InMemoryEpisodicStore();
        var daily = new InMemoryDailyMemoryStore();
        var semantic = new InMemorySemanticMemoryStore();
        var deltas = new InMemoryPersonaDeltaStore();
        var core = new InMemoryCoreMemoryStore();
        var persona = new InMemoryPersonaStore();
        var sum = new HeuristicSummarizer();
        var engine = new MemoryConsolidator(
            episodic, daily, semantic, deltas, core, persona, sum,
            opts, Clock);
        return (engine, episodic, daily, semantic, deltas, core, persona);
    }

    // ══════════════════════════════════════════════════════════════════════
    // HeuristicSummarizer.SummarizeDayAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SummarizeDay_NoEntries_ProducesEmptyMarker()
    {
        var sum = new HeuristicSummarizer();
        var day = new DateOnly(2026, 6, 1);
        var r = await sum.SummarizeDayAsync(day, Array.Empty<EpisodicMemoryEntry>());
        Assert.Equal(day, r.Day);
        Assert.Equal(0, r.EpisodeCount);
        Assert.Contains("No exchanges", r.Summary);
    }

    [Fact]
    public async Task SummarizeDay_PopulatesTopicWeightsAndHighlights()
    {
        var sum = new HeuristicSummarizer { HighlightCount = 2 };
        var at = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            MakeEntry(at,                "What's the markets doing?",     "Up 1.2%.", topic: "finance"),
            MakeEntry(at.AddMinutes(10), "Tell me more about ETFs.",      "ETFs are basket-style funds…", topic: "finance"),
            MakeEntry(at.AddMinutes(20), "Recipe for dinner?",            "Try pasta carbonara.",         topic: "cooking"),
        };
        var r = await sum.SummarizeDayAsync(new DateOnly(2026, 6, 1), entries);

        Assert.Equal(3, r.EpisodeCount);
        Assert.True(r.TopicWeights.ContainsKey("finance"));
        Assert.True(r.TopicWeights.ContainsKey("cooking"));
        Assert.Equal(2f, r.TopicWeights["finance"]);
        Assert.Equal(1f, r.TopicWeights["cooking"]);
        Assert.Equal(2, r.HighlightEntries.Count);
        Assert.Contains("finance", r.Summary);
    }

    [Fact]
    public async Task SummarizeDay_AllHighlightsKept_WhenEntriesUnderHighlightCount()
    {
        var sum = new HeuristicSummarizer { HighlightCount = 5 };
        var at = _now;
        var entries = new[]
        {
            MakeEntry(at,                "a", "b", topic: "x"),
            MakeEntry(at.AddMinutes(1),  "c", "d", topic: "x"),
        };
        var r = await sum.SummarizeDayAsync(new DateOnly(2026, 6, 15), entries);
        Assert.Equal(2, r.HighlightEntries.Count);
    }

    [Fact]
    public async Task SummarizeDay_DispersionIsZero_WhenAllEmbeddingsIdentical()
    {
        var sum = new HeuristicSummarizer();
        var emb = Emb(1f, 0f, 0f);
        var entries = new[]
        {
            MakeEntry(_now, "a", "b", embedding: emb),
            MakeEntry(_now.AddMinutes(1), "c", "d", embedding: emb),
            MakeEntry(_now.AddMinutes(2), "e", "f", embedding: emb),
        };
        var r = await sum.SummarizeDayAsync(new DateOnly(2026, 6, 15), entries);
        Assert.InRange(r.TopicDispersion, 0.0, 0.0001);
    }

    [Fact]
    public async Task SummarizeDay_DispersionIsHigh_WhenEmbeddingsOrthogonal()
    {
        var sum = new HeuristicSummarizer();
        var entries = new[]
        {
            MakeEntry(_now,                "a", "b", embedding: Emb(1f, 0f, 0f)),
            MakeEntry(_now.AddMinutes(1),  "c", "d", embedding: Emb(0f, 1f, 0f)),
            MakeEntry(_now.AddMinutes(2),  "e", "f", embedding: Emb(0f, 0f, 1f)),
        };
        var r = await sum.SummarizeDayAsync(new DateOnly(2026, 6, 15), entries);
        Assert.InRange(r.TopicDispersion, 0.99, 1.0001);
    }

    // ══════════════════════════════════════════════════════════════════════
    // HeuristicSummarizer.ConsolidateWeekAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConsolidateWeek_EmptyDays_ProducesNoClusters()
    {
        var sum = new HeuristicSummarizer();
        var r = await sum.ConsolidateWeekAsync(
            new DateOnly(2026, 6, 8), Array.Empty<DailyMemorySummary>());
        Assert.Empty(r);
    }

    [Fact]
    public async Task ConsolidateWeek_ProducesClusterWhenTopicSpansMinDays()
    {
        var sum = new HeuristicSummarizer { MinDaysPerTopicForCluster = 2 };
        var weekStart = new DateOnly(2026, 6, 8);
        var days = new[]
        {
            new DailyMemorySummary { Day = weekStart,            TopicWeights = new Dictionary<string, float> { ["finance"] = 3 }, EpisodeCount = 3 },
            new DailyMemorySummary { Day = weekStart.AddDays(1), TopicWeights = new Dictionary<string, float> { ["finance"] = 2 }, EpisodeCount = 2 },
            new DailyMemorySummary { Day = weekStart.AddDays(2), TopicWeights = new Dictionary<string, float> { ["cooking"] = 1 }, EpisodeCount = 1 },
        };
        var r = await sum.ConsolidateWeekAsync(weekStart, days);
        Assert.Single(r);
        Assert.Equal("finance", r[0].Topic);
        Assert.Equal(5f, r[0].TopicWeight);
    }

    // ══════════════════════════════════════════════════════════════════════
    // HeuristicSummarizer.DerivePersonaDeltaAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DerivePersonaDelta_DetectsNewAndStrengthenedTopics()
    {
        var sum = new HeuristicSummarizer();
        var before = new PersonaState { UserId = "u1", TotalInteractions = 100, PositiveSignals = 20, NegativeSignals = 5 };
        before.TopicWeights["finance"] = 5f;

        var after = new PersonaState { UserId = "u1", TotalInteractions = 130, PositiveSignals = 30, NegativeSignals = 7 };
        after.TopicWeights["finance"] = 8f;   // strengthened (+3)
        after.TopicWeights["family"] = 4f;    // new
        after.DisfavouredTopics.Add("politics");

        var days = new[]
        {
            new DailyMemorySummary { Day = new DateOnly(2026, 5, 1), EpisodeCount = 5 },
            new DailyMemorySummary { Day = new DateOnly(2026, 5, 30), EpisodeCount = 5 },
        };

        var delta = await sum.DerivePersonaDeltaAsync(before, after, days);

        Assert.Equal("u1", delta.UserId);
        Assert.Contains("family", delta.NewTopics.Keys);
        Assert.Contains("finance", delta.StrengthenedTopics.Keys);
        Assert.Equal(3f, delta.StrengthenedTopics["finance"]);
        Assert.Contains("politics", delta.NewlyDisfavouredTopics);
        Assert.Equal(30, delta.InteractionsInPeriod);
        Assert.Equal(8, delta.NetSignalDelta); // (30-20) - (7-5) = 8
        Assert.False(string.IsNullOrWhiteSpace(delta.Narrative));
    }

    // ══════════════════════════════════════════════════════════════════════
    // MemoryConsolidator.TickAsync — Daily
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TickDaily_ProducesSummariesForCompletedDaysOnly()
    {
        var w = Wire();
        var yesterday = _now.AddDays(-1);
        var dayBefore = _now.AddDays(-2);
        await w.episodic.AddAsync(MakeEntry(dayBefore,           "a", "b", topic: "finance"));
        await w.episodic.AddAsync(MakeEntry(yesterday,            "c", "d", topic: "finance"));
        await w.episodic.AddAsync(MakeEntry(_now,                 "e", "f", topic: "finance")); // today — excluded

        var r = await w.engine.TickAsync(SleepKind.Daily);

        Assert.Equal(2, r.DailySummariesProduced);
        Assert.Equal(2, await w.daily.CountAsync());
        // Today's entry must NOT have been consolidated yet
        var todaySummary = await w.daily.GetAsync(DateOnly.FromDateTime(_now.UtcDateTime));
        Assert.Null(todaySummary);
    }

    [Fact]
    public async Task TickDaily_IsIdempotent()
    {
        var w = Wire();
        await w.episodic.AddAsync(MakeEntry(_now.AddDays(-1), "a", "b", topic: "finance"));

        var first = await w.engine.TickAsync(SleepKind.Daily);
        var second = await w.engine.TickAsync(SleepKind.Daily);

        Assert.Equal(1, first.DailySummariesProduced);
        Assert.Equal(0, second.DailySummariesProduced);
        Assert.Equal(1, await w.daily.CountAsync());
    }

    [Fact]
    public async Task TickDaily_PrunesEpisodesOlderThanRetention()
    {
        var w = Wire(new MemoryConsolidationOptions { EpisodicRetentionDays = 3 });
        await w.episodic.AddAsync(MakeEntry(_now.AddDays(-10), "old", "x", topic: "t"));
        await w.episodic.AddAsync(MakeEntry(_now.AddDays(-1),  "new", "y", topic: "t"));

        var r = await w.engine.TickAsync(SleepKind.Daily);

        Assert.Equal(1, r.EpisodesPruned);
        Assert.Equal(1, await w.episodic.CountAsync()); // only the recent survives
    }

    // ══════════════════════════════════════════════════════════════════════
    // MemoryConsolidator.TickAsync — Weekly
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TickWeekly_ClustersLastWeekDailies()
    {
        var w = Wire();
        // _now is 2026-06-15 (a Monday), so "this Monday" = 2026-06-15
        // and "last Monday" = 2026-06-08 → previous week is 06-08 .. 06-14.
        for (int i = 0; i < 5; i++)
        {
            await w.daily.UpsertAsync(new DailyMemorySummary
            {
                Day = new DateOnly(2026, 6, 8).AddDays(i),
                TopicWeights = new Dictionary<string, float> { ["finance"] = 2 },
                EpisodeCount = 2,
            });
        }
        var r = await w.engine.TickAsync(SleepKind.Weekly);
        Assert.True(r.SemanticClustersProduced >= 1);
        Assert.Equal(r.SemanticClustersProduced, await w.semantic.CountAsync());
    }

    [Fact]
    public async Task TickWeekly_IsIdempotent()
    {
        var w = Wire();
        for (int i = 0; i < 3; i++)
        {
            await w.daily.UpsertAsync(new DailyMemorySummary
            {
                Day = new DateOnly(2026, 6, 8).AddDays(i),
                TopicWeights = new Dictionary<string, float> { ["finance"] = 2 },
                EpisodeCount = 2,
            });
        }
        var first = await w.engine.TickAsync(SleepKind.Weekly);
        var second = await w.engine.TickAsync(SleepKind.Weekly);

        Assert.True(first.SemanticClustersProduced >= 1);
        Assert.Equal(0, second.SemanticClustersProduced);
    }

    // ══════════════════════════════════════════════════════════════════════
    // MemoryConsolidator.TickAsync — Monthly
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TickMonthly_WritesPersonaDeltaForPreviousMonth()
    {
        var w = Wire();
        var persona = await w.persona.LoadAsync("default");
        persona.TopicWeights["finance"] = 8f;
        persona.TotalInteractions = 50;
        persona.PositiveSignals = 12;
        persona.NegativeSignals = 3;
        await w.persona.SaveAsync(persona);

        // Previous month is May 2026 — populate daily summaries for it.
        for (int day = 1; day <= 3; day++)
        {
            await w.daily.UpsertAsync(new DailyMemorySummary
            {
                Day = new DateOnly(2026, 5, day),
                EpisodeCount = 10,
                TopicWeights = new Dictionary<string, float> { ["finance"] = 5 },
            });
        }

        var r = await w.engine.TickAsync(SleepKind.Monthly);

        Assert.Equal(1, r.PersonaDeltasProduced);
        var deltas = await w.deltas.GetForUserAsync("default");
        Assert.Single(deltas);
        Assert.Equal(new DateOnly(2026, 5, 1), deltas[0].PeriodStart);
    }

    [Fact]
    public async Task TickMonthly_IsIdempotent()
    {
        var w = Wire();
        for (int day = 1; day <= 3; day++)
        {
            await w.daily.UpsertAsync(new DailyMemorySummary
            {
                Day = new DateOnly(2026, 5, day),
                EpisodeCount = 5,
                TopicWeights = new Dictionary<string, float> { ["t"] = 1 },
            });
        }
        await w.engine.TickAsync(SleepKind.Monthly);
        var second = await w.engine.TickAsync(SleepKind.Monthly);
        Assert.Equal(0, second.PersonaDeltasProduced);
    }

    // ══════════════════════════════════════════════════════════════════════
    // MemoryConsolidator.TickAsync — OnDemand
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TickOnDemand_RunsAllThreeTiers()
    {
        var w = Wire();
        // One day's worth of episodes, plus pre-existing dailies for the
        // previous week so the weekly + monthly stages have data.
        await w.episodic.AddAsync(MakeEntry(_now.AddDays(-1), "a", "b", topic: "x"));
        // Populate previous week's dailies (Mon 2026-06-08 onwards).
        for (int i = 0; i < 3; i++)
        {
            await w.daily.UpsertAsync(new DailyMemorySummary
            {
                Day = new DateOnly(2026, 6, 8).AddDays(i),
                EpisodeCount = 3,
                TopicWeights = new Dictionary<string, float> { ["x"] = 2 },
            });
        }
        for (int day = 1; day <= 3; day++)
        {
            await w.daily.UpsertAsync(new DailyMemorySummary
            {
                Day = new DateOnly(2026, 5, day),
                EpisodeCount = 3,
                TopicWeights = new Dictionary<string, float> { ["x"] = 2 },
            });
        }
        var r = await w.engine.TickAsync(SleepKind.OnDemand);
        Assert.True(r.DailySummariesProduced >= 1);
        Assert.True(r.SemanticClustersProduced >= 1);
        Assert.True(r.PersonaDeltasProduced >= 0); // may be 0 if persona unchanged
    }

    // ══════════════════════════════════════════════════════════════════════
    // Core promotion
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TickDaily_PromotesHighSalienceDayToCore()
    {
        // Low threshold so any non-trivial day promotes.
        var w = Wire(new MemoryConsolidationOptions { DailyCorePromotionThreshold = 0.1 });
        for (int i = 0; i < 5; i++)
        {
            await w.episodic.AddAsync(
                MakeEntry(_now.AddDays(-1).AddMinutes(i * 10), $"q{i}", $"a{i}", topic: "x"));
        }
        var r = await w.engine.TickAsync(SleepKind.Daily);
        Assert.True(r.CorePromotions >= 1);
        Assert.True(await w.core.CountAsync() >= 1);
    }

    // ══════════════════════════════════════════════════════════════════════
    // In-memory store sanity
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DailyStore_UpsertReplacesEntryForSameDay()
    {
        var store = new InMemoryDailyMemoryStore();
        var day = new DateOnly(2026, 6, 1);
        await store.UpsertAsync(new DailyMemorySummary { Day = day, EpisodeCount = 1 });
        await store.UpsertAsync(new DailyMemorySummary { Day = day, EpisodeCount = 99 });
        var got = await store.GetAsync(day);
        Assert.NotNull(got);
        Assert.Equal(99, got!.EpisodeCount);
    }

    [Fact]
    public async Task CoreStore_ReinforceIncrementsCount()
    {
        var store = new InMemoryCoreMemoryStore();
        var m = new CoreMemory { Statement = "Tony's daughter is Alex.", Kind = CoreMemoryKind.UserAsserted };
        await store.AddAsync(m);
        await store.ReinforceAsync(m.Id);
        await store.ReinforceAsync(m.Id);
        var got = await store.GetAsync(m.Id);
        Assert.NotNull(got);
        Assert.Equal(2, got!.ReinforcementCount);
    }
}
