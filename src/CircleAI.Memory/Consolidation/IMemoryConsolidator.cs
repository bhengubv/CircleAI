// IMemoryConsolidator.cs
//
// The sleep-cycle engine. Promotes episodic → daily → weekly → monthly
// (persona delta) → core. Drives the IMemorySummarizer for the heavy
// summarisation work; owns scheduling, retention, and core-promotion
// decisions.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Outcome of a single <see cref="IMemoryConsolidator"/> tick.
/// </summary>
public sealed record ConsolidationOutcome(
    SleepKind Kind,
    int DailySummariesProduced,
    int SemanticClustersProduced,
    int PersonaDeltasProduced,
    int CorePromotions,
    int EpisodesPruned,
    int DailiesPruned,
    int SemanticsPruned,
    DateTimeOffset RanAtUtc);

/// <summary>
/// Promotes lower-tier memory into higher tiers and enforces retention.
/// Hosts call <see cref="TickAsync"/> on a schedule (daily / weekly /
/// monthly) or trigger an on-demand consolidation.
/// </summary>
public interface IMemoryConsolidator
{
    /// <summary>
    /// Runs the consolidation pass for the given <paramref name="kind"/>.
    /// <see cref="SleepKind.OnDemand"/> runs every tier that has work pending.
    /// Returns the breakdown of what was produced and pruned.
    /// </summary>
    Task<ConsolidationOutcome> TickAsync(SleepKind kind, CancellationToken ct = default);
}
