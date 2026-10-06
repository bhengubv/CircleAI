// IMemorySummarizer.cs
//
// Strategy seam — the consolidator calls into a summariser to produce the
// human-readable text for each tier. CircleAI ships HeuristicSummarizer in
// the box (no LLM dependency); the host can register a custom implementation
// that calls into CircleAI.Inference's IChatGenerator for richer summaries.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Produces the text + scores for each consolidation tier. Implementations
/// may be heuristic (no LLM, ships in CircleAI.Memory) or LLM-backed (the
/// host wires an adapter over <c>CircleAI.Inference.IChatGenerator</c>).
/// </summary>
public interface IMemorySummarizer
{
    /// <summary>
    /// Produces a <see cref="DailyMemorySummary"/> from the day's episodic
    /// entries. Implementations are responsible for picking highlight
    /// exchanges, computing topic weights, topic dispersion, and salience.
    /// </summary>
    Task<DailyMemorySummary> SummarizeDayAsync(
        System.DateOnly day,
        IReadOnlyList<EpisodicMemoryEntry> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Produces one or more <see cref="SemanticMemoryCluster"/> records from
    /// a week's daily summaries. Returns an empty list when nothing meaningful
    /// can be clustered (e.g. no daily summaries at all).
    /// </summary>
    Task<IReadOnlyList<SemanticMemoryCluster>> ConsolidateWeekAsync(
        System.DateOnly weekStartingMonday,
        IReadOnlyList<DailyMemorySummary> daysInWeek,
        CancellationToken ct = default);

    /// <summary>
    /// Computes the <see cref="PersonaDeltaSnapshot"/> across the period
    /// covered by <paramref name="daysInPeriod"/>, given the persona state
    /// at the start and end of the period.
    /// </summary>
    Task<PersonaDeltaSnapshot> DerivePersonaDeltaAsync(
        PersonaState before,
        PersonaState after,
        IReadOnlyList<DailyMemorySummary> daysInPeriod,
        CancellationToken ct = default);
}
