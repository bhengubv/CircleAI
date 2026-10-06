// HeuristicSummarizer.cs
//
// No-LLM IMemorySummarizer. Produces summaries entirely from structural
// signals — embedding clustering, topic-weight aggregation, length-and-recency
// salience. Always runs offline, no model required, no token cost.
//
// Hosts that want richer prose summaries can register their own LLM-backed
// IMemorySummarizer; this implementation is the always-on fallback and the
// default registered by AddMemoryConsolidator().

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Heuristic <see cref="IMemorySummarizer"/> that requires no LLM.
/// </summary>
public sealed class HeuristicSummarizer : IMemorySummarizer
{
    /// <summary>
    /// Maximum number of high-salience verbatim entries kept on each
    /// <see cref="DailyMemorySummary"/>.
    /// </summary>
    public int HighlightCount { get; init; } = 5;

    /// <summary>
    /// Minimum number of contributing days a topic must appear in across a
    /// week before it is eligible to form a <see cref="SemanticMemoryCluster"/>.
    /// </summary>
    public int MinDaysPerTopicForCluster { get; init; } = 2;

    // ── IMemorySummarizer.SummarizeDayAsync ──────────────────────────────

    public Task<DailyMemorySummary> SummarizeDayAsync(
        DateOnly day,
        IReadOnlyList<EpisodicMemoryEntry> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ct.ThrowIfCancellationRequested();

        if (entries.Count == 0)
        {
            return Task.FromResult(new DailyMemorySummary
            {
                Day = day,
                Summary = $"No exchanges recorded on {day:yyyy-MM-dd}.",
                EpisodeCount = 0,
            });
        }

        var topicWeights = AggregateTopicWeights(entries);
        var dispersion = MeanPairwiseCosineDistance(entries);
        var highlights = SelectHighlights(entries, HighlightCount);
        var salience = ComputeDailySalience(entries.Count, topicWeights, dispersion);
        var summary = BuildDailySummaryText(day, entries.Count, topicWeights, highlights);

        return Task.FromResult(new DailyMemorySummary
        {
            Day = day,
            Summary = summary,
            HighlightEntries = highlights,
            EpisodeCount = entries.Count,
            TopicWeights = topicWeights,
            TopicDispersion = dispersion,
            Salience = salience,
        });
    }

    // ── IMemorySummarizer.ConsolidateWeekAsync ───────────────────────────

    public Task<IReadOnlyList<SemanticMemoryCluster>> ConsolidateWeekAsync(
        DateOnly weekStartingMonday,
        IReadOnlyList<DailyMemorySummary> daysInWeek,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(daysInWeek);
        ct.ThrowIfCancellationRequested();

        if (daysInWeek.Count == 0)
        {
            IReadOnlyList<SemanticMemoryCluster> empty = Array.Empty<SemanticMemoryCluster>();
            return Task.FromResult(empty);
        }

        // Tally how many days each topic appeared in and its cumulative weight.
        var topicToDays = new Dictionary<string, List<DailyMemorySummary>>(StringComparer.OrdinalIgnoreCase);
        var topicToWeight = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in daysInWeek)
        {
            foreach (var (topic, w) in d.TopicWeights)
            {
                if (!topicToDays.TryGetValue(topic, out var list))
                {
                    list = new List<DailyMemorySummary>();
                    topicToDays[topic] = list;
                }
                list.Add(d);

                topicToWeight.TryGetValue(topic, out var existing);
                topicToWeight[topic] = existing + w;
            }
        }

        var totalWeight = topicToWeight.Values.Sum();
        if (totalWeight <= 0f) totalWeight = 1f;

        var clusters = new List<SemanticMemoryCluster>();
        foreach (var topic in topicToWeight.Keys
                     .OrderByDescending(t => topicToWeight[t]))
        {
            var contributingDays = topicToDays[topic];
            if (contributingDays.Count < MinDaysPerTopicForCluster) continue;

            var centroid = CentroidOfHighlights(contributingDays);
            var weight = topicToWeight[topic];
            var clusterSalience = Math.Min(1.0, (weight / totalWeight) +
                                               (contributingDays.Count / 7.0) * 0.25);

            clusters.Add(new SemanticMemoryCluster
            {
                WeekStartingMonday = weekStartingMonday,
                Topic = topic,
                Summary = BuildWeeklyClusterText(topic, contributingDays),
                CentroidEmbedding = centroid,
                SourceDailyIds = contributingDays.Select(d => d.Id).ToList(),
                TopicWeight = weight,
                Salience = clusterSalience,
            });
        }

        IReadOnlyList<SemanticMemoryCluster> result = clusters;
        return Task.FromResult(result);
    }

    // ── IMemorySummarizer.DerivePersonaDeltaAsync ────────────────────────

    public Task<PersonaDeltaSnapshot> DerivePersonaDeltaAsync(
        PersonaState before,
        PersonaState after,
        IReadOnlyList<DailyMemorySummary> daysInPeriod,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(daysInPeriod);
        ct.ThrowIfCancellationRequested();

        var newTopics = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var strengthened = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (topic, afterW) in after.TopicWeights)
        {
            before.TopicWeights.TryGetValue(topic, out var beforeW);
            var delta = afterW - beforeW;
            if (beforeW <= 0f && afterW > 0f)
            {
                newTopics[topic] = afterW;
            }
            else if (delta > 0f)
            {
                strengthened[topic] = delta;
            }
        }

        var disfavouredNew = after.DisfavouredTopics
            .Where(t => !before.DisfavouredTopics.Contains(t))
            .ToList();

        var netSignals = (after.PositiveSignals - before.PositiveSignals)
                       - (after.NegativeSignals - before.NegativeSignals);
        var interactions = after.TotalInteractions - before.TotalInteractions;

        var periodStart = daysInPeriod.Count > 0
            ? daysInPeriod.Min(d => d.Day)
            : DateOnly.FromDateTime(after.LastUpdatedUtc.UtcDateTime);
        var periodEnd = daysInPeriod.Count > 0
            ? daysInPeriod.Max(d => d.Day)
            : DateOnly.FromDateTime(after.LastUpdatedUtc.UtcDateTime);

        var narrative = BuildPersonaNarrative(
            before, after, newTopics, strengthened, disfavouredNew,
            netSignals, interactions, periodStart, periodEnd);

        return Task.FromResult(new PersonaDeltaSnapshot
        {
            UserId = after.UserId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            VerbosityBefore = before.Verbosity,
            VerbosityAfter = after.Verbosity,
            FormalityBefore = before.Formality,
            FormalityAfter = after.Formality,
            NewTopics = newTopics,
            StrengthenedTopics = strengthened,
            NewlyDisfavouredTopics = disfavouredNew,
            NetSignalDelta = netSignals,
            InteractionsInPeriod = interactions,
            Narrative = narrative,
        });
    }

    // ── Helpers — topic + dispersion ─────────────────────────────────────

    private static IReadOnlyDictionary<string, float> AggregateTopicWeights(
        IReadOnlyList<EpisodicMemoryEntry> entries)
    {
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Tags is null) continue;
            // Recognised tag keys: "topic", "topics" (pipe-delimited)
            if (e.Tags.TryGetValue("topic", out var t) && !string.IsNullOrWhiteSpace(t))
            {
                AccumulateTopic(weights, t, 1f);
            }
            if (e.Tags.TryGetValue("topics", out var multi) && !string.IsNullOrWhiteSpace(multi))
            {
                foreach (var p in multi.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    AccumulateTopic(weights, p, 1f);
            }
        }
        return weights;
    }

    private static void AccumulateTopic(IDictionary<string, float> dict, string topic, float weight)
    {
        topic = topic.Trim().ToLowerInvariant();
        if (topic.Length == 0) return;
        dict.TryGetValue(topic, out var existing);
        dict[topic] = existing + weight;
    }

    private static double MeanPairwiseCosineDistance(IReadOnlyList<EpisodicMemoryEntry> entries)
    {
        var withEmbeddings = entries.Where(e => e.Embedding is { Length: > 0 }).ToList();
        if (withEmbeddings.Count < 2) return 0.0;

        double total = 0;
        int pairs = 0;
        for (int i = 0; i < withEmbeddings.Count; i++)
        {
            for (int j = i + 1; j < withEmbeddings.Count; j++)
            {
                var sim = CosineSimilarity.Score(
                    withEmbeddings[i].Embedding!, withEmbeddings[j].Embedding!);
                total += 1.0 - Math.Clamp(sim, -1.0, 1.0);
                pairs++;
            }
        }
        return pairs == 0 ? 0.0 : Math.Clamp(total / pairs, 0.0, 1.0);
    }

    private static IReadOnlyList<EpisodicMemoryEntry> SelectHighlights(
        IReadOnlyList<EpisodicMemoryEntry> entries, int count)
    {
        if (entries.Count <= count)
            return entries.OrderBy(e => e.RecordedAtUtc).ToList();

        // Salience proxy: combined text length (more words → typically more
        // substantive turn) plus a uniqueness bonus when the embedding is
        // furthest from the mean. Ties broken by recency.
        var ordered = entries
            .Select(e => (entry: e, score: EntrySalienceProxy(e, entries)))
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.entry.RecordedAtUtc)
            .Take(count)
            .Select(x => x.entry)
            .OrderBy(e => e.RecordedAtUtc)
            .ToList();
        return ordered;
    }

    private static double EntrySalienceProxy(
        EpisodicMemoryEntry entry, IReadOnlyList<EpisodicMemoryEntry> all)
    {
        var lengthScore = Math.Min(1.0, (entry.UserText.Length + entry.AssistantText.Length) / 800.0);
        var uniquenessScore = 0.5;
        if (entry.Embedding is { Length: > 0 })
        {
            var others = all.Where(e => e.Id != entry.Id && e.Embedding is { Length: > 0 }).ToList();
            if (others.Count > 0)
            {
                var meanSim = others.Average(e =>
                    CosineSimilarity.Score(entry.Embedding!, e.Embedding!));
                uniquenessScore = 1.0 - Math.Clamp(meanSim, -1.0, 1.0);
            }
        }
        return (lengthScore * 0.6) + (uniquenessScore * 0.4);
    }

    private static double ComputeDailySalience(
        int episodeCount,
        IReadOnlyDictionary<string, float> topicWeights,
        double dispersion)
    {
        var volumeScore = Math.Min(1.0, episodeCount / 30.0);
        var topicConcentration = topicWeights.Count == 0
            ? 0.5
            : Math.Min(1.0, topicWeights.Values.Max() / Math.Max(1f, topicWeights.Values.Sum()));
        return (volumeScore * 0.4) + (dispersion * 0.3) + (topicConcentration * 0.3);
    }

    private static float[]? CentroidOfHighlights(IReadOnlyList<DailyMemorySummary> days)
    {
        var allEmbeddings = days
            .SelectMany(d => d.HighlightEntries)
            .Where(e => e.Embedding is { Length: > 0 })
            .Select(e => e.Embedding!)
            .ToList();

        if (allEmbeddings.Count == 0) return null;
        var dim = allEmbeddings[0].Length;
        var centroid = new float[dim];
        foreach (var e in allEmbeddings)
            for (var i = 0; i < dim && i < e.Length; i++)
                centroid[i] += e[i];
        for (var i = 0; i < dim; i++) centroid[i] /= allEmbeddings.Count;
        return centroid;
    }

    // ── Text builders ────────────────────────────────────────────────────

    private static string BuildDailySummaryText(
        DateOnly day, int count,
        IReadOnlyDictionary<string, float> topics,
        IReadOnlyList<EpisodicMemoryEntry> highlights)
    {
        var topTopics = topics
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        var topicsClause = topTopics.Count > 0
            ? $" Top topics: {string.Join(", ", topTopics)}."
            : string.Empty;

        var highlightClause = highlights.Count > 0
            ? $" Standout moment: \"{Truncate(highlights[0].UserText, 120)}\"."
            : string.Empty;

        return $"On {day:yyyy-MM-dd} you had {count} " +
               (count == 1 ? "exchange." : "exchanges.") +
               topicsClause + highlightClause;
    }

    private static string BuildWeeklyClusterText(
        string topic, IReadOnlyList<DailyMemorySummary> contributingDays)
    {
        var totalEpisodes = contributingDays.Sum(d => d.EpisodeCount);
        return $"Across {contributingDays.Count} days this week you returned to " +
               $"\"{topic}\" — {totalEpisodes} exchanges in total.";
    }

    private static string BuildPersonaNarrative(
        PersonaState before, PersonaState after,
        IReadOnlyDictionary<string, float> newTopics,
        IReadOnlyDictionary<string, float> strengthened,
        IReadOnlyList<string> disfavoured,
        int netSignals, int interactions,
        DateOnly periodStart, DateOnly periodEnd)
    {
        var parts = new List<string>();
        parts.Add($"Between {periodStart:yyyy-MM-dd} and {periodEnd:yyyy-MM-dd}, " +
                  $"{interactions} interactions were recorded.");
        if (newTopics.Count > 0)
        {
            parts.Add("New interests appeared: " +
                      string.Join(", ", newTopics.OrderByDescending(kv => kv.Value)
                          .Take(3).Select(kv => kv.Key)) + ".");
        }
        if (strengthened.Count > 0)
        {
            parts.Add("Existing interests deepened around " +
                      string.Join(", ", strengthened.OrderByDescending(kv => kv.Value)
                          .Take(3).Select(kv => kv.Key)) + ".");
        }
        if (disfavoured.Count > 0)
        {
            parts.Add("Topics now avoided: " + string.Join(", ", disfavoured) + ".");
        }
        if (before.Verbosity != after.Verbosity)
        {
            parts.Add($"Preferred verbosity shifted from {before.Verbosity} to {after.Verbosity}.");
        }
        if (before.Formality != after.Formality)
        {
            parts.Add($"Preferred tone shifted from {before.Formality} to {after.Formality}.");
        }
        if (netSignals != 0)
        {
            parts.Add(netSignals > 0
                ? $"Net feedback was positive (+{netSignals})."
                : $"Net feedback was negative ({netSignals}).");
        }
        return string.Join(" ", parts);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Length <= max) return s;
        return s[..max].TrimEnd() + "…";
    }
}
