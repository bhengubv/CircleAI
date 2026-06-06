// IDailyMemoryStore.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Persistent store for tier-2 daily summaries.
/// </summary>
public interface IDailyMemoryStore
{
    /// <summary>Adds a daily summary. Replaces any existing entry for the same day.</summary>
    Task UpsertAsync(DailyMemorySummary summary, CancellationToken ct = default);

    /// <summary>Returns the summary for the given day, or null when none exists.</summary>
    Task<DailyMemorySummary?> GetAsync(DateOnly day, CancellationToken ct = default);

    /// <summary>Returns all summaries between <paramref name="fromInclusive"/> and <paramref name="toInclusive"/>.</summary>
    Task<IReadOnlyList<DailyMemorySummary>> GetRangeAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);

    /// <summary>Removes summaries older than <paramref name="cutoff"/>. Returns count removed.</summary>
    Task<int> PruneOlderThanAsync(DateOnly cutoff, CancellationToken ct = default);

    /// <summary>Total summaries currently stored.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
