// MemoryConsolidator.cs
//
// The orchestration engine. Pure scheduling + promotion logic; delegates the
// actual summarisation to IMemorySummarizer. Idempotent — calling Tick(Daily)
// twice for the same day produces one summary (the second call replaces the
// first via UpsertAsync).
//
// All time decisions go through a configurable clock so tests are deterministic
// without Task.Delay.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Retention windows + core-promotion thresholds. Defaults follow the
/// hierarchical-memory plan: 7-day episodic window, 30-day daily window,
/// 12-month semantic window, salience ≥ 0.80 promotes to core.
/// </summary>
public sealed class MemoryConsolidationOptions
{
    /// <summary>How many days of episodic entries to retain after they've been summarised.</summary>
    public int EpisodicRetentionDays { get; init; } = 7;

    /// <summary>How many days of daily summaries to retain after weekly consolidation.</summary>
    public int DailyRetentionDays { get; init; } = 30;

    /// <summary>How many days of semantic clusters to retain.</summary>
    public int SemanticRetentionDays { get; init; } = 365;

    /// <summary>Salience threshold above which daily summaries promote to core.</summary>
    public double DailyCorePromotionThreshold { get; init; } = 0.80;

    /// <summary>Salience threshold above which weekly clusters promote to core.</summary>
    public double WeeklyCorePromotionThreshold { get; init; } = 0.75;
}

/// <summary>
/// Default <see cref="IMemoryConsolidator"/> implementation.
/// </summary>
public sealed class MemoryConsolidator : IMemoryConsolidator
{
    private readonly IEpisodicMemoryStore _episodic;
    private readonly IDailyMemoryStore _daily;
    private readonly ISemanticMemoryStore _semantic;
    private readonly IPersonaDeltaStore _personaDelta;
    private readonly ICoreMemoryStore _core;
    private readonly IPersonaStore _personaStore;
    private readonly IMemorySummarizer _summarizer;
    private readonly MemoryConsolidationOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _userId;

    public MemoryConsolidator(
        IEpisodicMemoryStore episodic,
        IDailyMemoryStore daily,
        ISemanticMemoryStore semantic,
        IPersonaDeltaStore personaDelta,
        ICoreMemoryStore core,
        IPersonaStore personaStore,
        IMemorySummarizer summarizer,
        MemoryConsolidationOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        string userId = "default")
    {
        _episodic = episodic ?? throw new ArgumentNullException(nameof(episodic));
        _daily = daily ?? throw new ArgumentNullException(nameof(daily));
        _semantic = semantic ?? throw new ArgumentNullException(nameof(semantic));
        _personaDelta = personaDelta ?? throw new ArgumentNullException(nameof(personaDelta));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _personaStore = personaStore ?? throw new ArgumentNullException(nameof(personaStore));
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _options = options ?? new MemoryConsolidationOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _userId = userId;
    }

    public async Task<ConsolidationOutcome> TickAsync(SleepKind kind, CancellationToken ct = default)
    {
        var now = _clock();
        int dailies = 0, clusters = 0, deltas = 0, corePromoted = 0;
        int episodesPruned = 0, dailiesPruned = 0, semanticsPruned = 0;

        if (kind is SleepKind.Daily or SleepKind.OnDemand)
        {
            (dailies, var promotedFromDaily) = await RunDailyAsync(now, ct).ConfigureAwait(false);
            corePromoted += promotedFromDaily;
            episodesPruned += await PruneEpisodicAsync(now, ct).ConfigureAwait(false);
        }

        if (kind is SleepKind.Weekly or SleepKind.OnDemand)
        {
            (clusters, var promotedFromWeekly) = await RunWeeklyAsync(now, ct).ConfigureAwait(false);
            corePromoted += promotedFromWeekly;
            dailiesPruned += await PruneDailiesAsync(now, ct).ConfigureAwait(false);
        }

        if (kind is SleepKind.Monthly or SleepKind.OnDemand)
        {
            deltas = await RunMonthlyAsync(now, ct).ConfigureAwait(false);
            semanticsPruned += await PruneSemanticsAsync(now, ct).ConfigureAwait(false);
        }

        return new ConsolidationOutcome(
            kind, dailies, clusters, deltas, corePromoted,
            episodesPruned, dailiesPruned, semanticsPruned, now);
    }

    // ── Daily pass ───────────────────────────────────────────────────────

    private async Task<(int produced, int promotedToCore)> RunDailyAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var recent = await _episodic.GetRecentAsync(int.MaxValue, ct).ConfigureAwait(false);
        if (recent.Count == 0) return (0, 0);

        // Group episodes by their calendar day (UTC).
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var byDay = recent
            .GroupBy(e => DateOnly.FromDateTime(e.RecordedAtUtc.UtcDateTime))
            .Where(g => g.Key < today) // only fully completed days
            .ToList();

        int produced = 0, promoted = 0;
        foreach (var group in byDay)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _daily.GetAsync(group.Key, ct).ConfigureAwait(false);
            if (existing is not null && existing.EpisodeCount == group.Count())
            {
                continue; // idempotent skip — already consolidated this day
            }

            var summary = await _summarizer.SummarizeDayAsync(
                group.Key, group.OrderBy(e => e.RecordedAtUtc).ToList(), ct).ConfigureAwait(false);
            await _daily.UpsertAsync(summary, ct).ConfigureAwait(false);
            produced++;

            if (summary.Salience >= _options.DailyCorePromotionThreshold)
            {
                promoted += await PromoteDailyToCoreAsync(summary, ct).ConfigureAwait(false);
            }
        }
        return (produced, promoted);
    }

    // ── Weekly pass ──────────────────────────────────────────────────────

    private async Task<(int produced, int promotedToCore)> RunWeeklyAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var thisMonday = MondayOf(today);
        var lastMonday = thisMonday.AddDays(-7);
        var lastSunday = lastMonday.AddDays(6);

        var lastWeek = await _daily.GetRangeAsync(lastMonday, lastSunday, ct).ConfigureAwait(false);
        if (lastWeek.Count == 0) return (0, 0);

        // Idempotency check: if we already have clusters for this week, skip.
        var existing = await _semantic.GetWeekAsync(lastMonday, ct).ConfigureAwait(false);
        if (existing.Count > 0) return (0, 0);

        var clusters = await _summarizer.ConsolidateWeekAsync(lastMonday, lastWeek, ct).ConfigureAwait(false);
        int promoted = 0;
        foreach (var c in clusters)
        {
            ct.ThrowIfCancellationRequested();
            await _semantic.AddAsync(c, ct).ConfigureAwait(false);
            if (c.Salience >= _options.WeeklyCorePromotionThreshold)
            {
                promoted += await PromoteClusterToCoreAsync(c, ct).ConfigureAwait(false);
            }
        }
        return (clusters.Count, promoted);
    }

    // ── Monthly pass ─────────────────────────────────────────────────────

    private async Task<int> RunMonthlyAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        // Consider the most recently completed full month.
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonthEnd = firstOfThisMonth.AddDays(-1);
        var lastMonthStart = new DateOnly(lastMonthEnd.Year, lastMonthEnd.Month, 1);

        // Idempotency: skip if we already have a delta whose PeriodStart falls
        // in the previous month. We compare by month-year (not exact dates)
        // because PeriodStart/End are inferred from the actual days present,
        // which may not exactly match the month boundaries.
        var existingDeltas = await _personaDelta.GetForUserAsync(_userId, ct).ConfigureAwait(false);
        if (existingDeltas.Any(d =>
                d.PeriodStart.Year == lastMonthStart.Year &&
                d.PeriodStart.Month == lastMonthStart.Month))
            return 0;

        var days = await _daily.GetRangeAsync(lastMonthStart, lastMonthEnd, ct).ConfigureAwait(false);
        if (days.Count == 0) return 0;

        var after = await _personaStore.LoadAsync(_userId, ct).ConfigureAwait(false)
                  ?? new PersonaState { UserId = _userId };
        // For "before", reconstruct from the most recent prior delta if one
        // exists; otherwise treat as a fresh persona.
        var prior = existingDeltas
            .Where(d => d.PeriodEnd < lastMonthStart)
            .OrderByDescending(d => d.PeriodEnd)
            .FirstOrDefault();
        var before = prior is null
            ? new PersonaState { UserId = _userId }
            : await ReconstructPersonaBeforeAsync(after, days, prior).ConfigureAwait(false);

        var delta = await _summarizer.DerivePersonaDeltaAsync(before, after, days, ct).ConfigureAwait(false);
        await _personaDelta.AddAsync(delta, ct).ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    /// Approximates the persona at the start of the period by subtracting the
    /// in-period gains from the current persona. Conservative — when in doubt
    /// it shows no change.
    /// </summary>
    private static Task<PersonaState> ReconstructPersonaBeforeAsync(
        PersonaState after,
        IReadOnlyList<DailyMemorySummary> daysInPeriod,
        PersonaDeltaSnapshot prior)
    {
        // For a v0.1 deterministic heuristic, treat the previous delta's
        // "After" state as the starting point.
        var before = new PersonaState
        {
            UserId = after.UserId,
            Verbosity = prior.VerbosityAfter,
            Formality = prior.FormalityAfter,
            PreferredLocale = after.PreferredLocale,
            TotalInteractions = after.TotalInteractions - daysInPeriod.Sum(d => d.EpisodeCount),
            PositiveSignals = Math.Max(0, after.PositiveSignals - prior.NetSignalDelta.ClampPositive()),
            NegativeSignals = after.NegativeSignals,
        };
        // Carry over topic weights minus the strongest in-period gains.
        foreach (var (topic, w) in after.TopicWeights)
        {
            if (prior.StrengthenedTopics.TryGetValue(topic, out var delta))
                before.TopicWeights[topic] = Math.Max(0f, w - delta);
            else
                before.TopicWeights[topic] = w;
        }
        foreach (var t in after.DisfavouredTopics)
            before.DisfavouredTopics.Add(t);
        return Task.FromResult(before);
    }

    // ── Core promotions ──────────────────────────────────────────────────

    private async Task<int> PromoteDailyToCoreAsync(DailyMemorySummary summary, CancellationToken ct)
    {
        var topTopic = summary.TopicWeights
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();
        var statement = topTopic.Key is null
            ? $"On {summary.Day:yyyy-MM-dd} an unusually meaningful day was recorded."
            : $"\"{topTopic.Key}\" mattered enough on {summary.Day:yyyy-MM-dd} to be remembered.";

        var memory = new CoreMemory
        {
            Statement = statement,
            Kind = CoreMemoryKind.HighSalience,
            Topic = topTopic.Key,
            Embedding = summary.HighlightEntries
                .Select(h => h.Embedding)
                .FirstOrDefault(e => e is { Length: > 0 }),
            SourceMemoryId = summary.Id,
        };
        await _core.AddAsync(memory, ct).ConfigureAwait(false);
        return 1;
    }

    private async Task<int> PromoteClusterToCoreAsync(SemanticMemoryCluster cluster, CancellationToken ct)
    {
        var memory = new CoreMemory
        {
            Statement = $"\"{cluster.Topic}\" has been a recurring theme " +
                        $"(week of {cluster.WeekStartingMonday:yyyy-MM-dd}).",
            Kind = CoreMemoryKind.PatternInferred,
            Topic = cluster.Topic,
            Embedding = cluster.CentroidEmbedding,
            SourceMemoryId = cluster.Id,
        };
        await _core.AddAsync(memory, ct).ConfigureAwait(false);
        return 1;
    }

    // ── Retention ────────────────────────────────────────────────────────

    private async Task<int> PruneEpisodicAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_options.EpisodicRetentionDays);
        return await _episodic.PruneOlderThanAsync(cutoff, ct).ConfigureAwait(false);
    }

    private async Task<int> PruneDailiesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-_options.DailyRetentionDays);
        return await _daily.PruneOlderThanAsync(cutoff, ct).ConfigureAwait(false);
    }

    private async Task<int> PruneSemanticsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-_options.SemanticRetentionDays);
        return await _semantic.PruneOlderThanAsync(cutoff, ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DateOnly MondayOf(DateOnly d)
    {
        var dow = d.DayOfWeek;
        var delta = ((int)dow + 6) % 7; // Sun=0..Sat=6 → Mon=0..Sun=6
        return d.AddDays(-delta);
    }
}

internal static class ConsolidatorExtensions
{
    public static int ClampPositive(this int v) => v < 0 ? 0 : v;
}
