// SemanticMemoryCluster.cs
//
// A cluster of related daily summaries, grouped by topic similarity.
// One step further from raw than DailyMemorySummary — at this tier the
// store no longer holds individual exchanges, only the topic gist + the
// daily-summary IDs that fed into it.

using System;
using System.Collections.Generic;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Topic-coherent cluster of daily summaries produced by the weekly
/// consolidation pass. This is the "semantic memory" tier — what the user
/// cares about across a week, not what was said on any given day.
/// </summary>
public sealed class SemanticMemoryCluster
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC time the cluster was produced.</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The week this cluster covers — represented by the Monday of that week
    /// (date only, UTC).
    /// </summary>
    public DateOnly WeekStartingMonday { get; init; }

    /// <summary>
    /// Dominant topic label for this cluster (e.g. "finance", "family").
    /// Picked by the summariser as the heaviest-weighted topic across the
    /// constituent daily summaries.
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>
    /// Short prose summary of the cluster's gist — what happened around this
    /// topic over the week.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Centroid embedding of the cluster (mean of its constituent embeddings)
    /// for cosine retrieval. Null when no embeddings were available.
    /// </summary>
    public float[]? CentroidEmbedding { get; init; }

    /// <summary>
    /// IDs of the daily summaries that contributed to this cluster.
    /// Lets the engine prune the daily tier independently while retaining
    /// the ability to drill back if the daily store still has them.
    /// </summary>
    public IReadOnlyList<Guid> SourceDailyIds { get; init; } = Array.Empty<Guid>();

    /// <summary>
    /// Aggregate weight of the topic across constituent days. Used by the
    /// monthly pass to detect what the user genuinely cares about.
    /// </summary>
    public float TopicWeight { get; init; }

    /// <summary>
    /// Salience score 0.0–1.0. High-salience clusters are candidates for
    /// promotion to <see cref="CoreMemory"/>.
    /// </summary>
    public double Salience { get; init; }
}
