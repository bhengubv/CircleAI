// DailyMemorySummary.cs
//
// One day's worth of episodic exchanges, compressed into a single record.
// Captures the gist (heuristic or LLM-generated summary text), the top
// salient exchanges (verbatim, so the high-signal moments survive), and
// the aggregate topic / affect signal for the day.

using System;
using System.Collections.Generic;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Compressed record of a single calendar day's worth of episodic memory.
/// Produced by <see cref="IMemorySummarizer.SummarizeDayAsync"/> and stored
/// by <see cref="IDailyMemoryStore"/>.
/// </summary>
public sealed class DailyMemorySummary
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The calendar day this summary covers (date portion only, UTC).
    /// </summary>
    public DateOnly Day { get; init; }

    /// <summary>UTC time the summary was produced.</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Short prose summary of the day's gist. Heuristic summarisers compose
    /// this from the top exchanges and topic keywords; LLM summarisers may
    /// produce richer narrative.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// The most salient verbatim exchanges from the day (typically 3–5).
    /// Preserves the high-signal moments so retrieval can still surface
    /// them word-for-word if needed.
    /// </summary>
    public IReadOnlyList<EpisodicMemoryEntry> HighlightEntries { get; init; }
        = Array.Empty<EpisodicMemoryEntry>();

    /// <summary>
    /// Total number of episodic entries collapsed into this summary.
    /// </summary>
    public int EpisodeCount { get; init; }

    /// <summary>
    /// Aggregated topic weights observed across the day's exchanges.
    /// Key = normalised topic label; value = accumulated weight.
    /// </summary>
    public IReadOnlyDictionary<string, float> TopicWeights { get; init; }
        = new Dictionary<string, float>();

    /// <summary>
    /// Mean cosine-distance dispersion of the day's embeddings — a rough
    /// indicator of how varied the day's conversations were. 0 = uniform,
    /// 1 = highly varied.
    /// </summary>
    public double TopicDispersion { get; init; }

    /// <summary>
    /// Salience score 0.0–1.0 assigned by the summariser. High-salience days
    /// are candidates for promotion to <see cref="CoreMemory"/>.
    /// </summary>
    public double Salience { get; init; }
}
