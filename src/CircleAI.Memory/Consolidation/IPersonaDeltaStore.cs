// IPersonaDeltaStore.cs

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Persistent store for tier-4 persona-delta snapshots.
/// Retained forever — these are the longitudinal record of how the AI's
/// model of the user has changed over time.
/// </summary>
public interface IPersonaDeltaStore
{
    /// <summary>Adds a delta snapshot.</summary>
    Task AddAsync(PersonaDeltaSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Returns all snapshots for the given user, ordered chronologically.
    /// </summary>
    Task<IReadOnlyList<PersonaDeltaSnapshot>> GetForUserAsync(
        string userId, CancellationToken ct = default);

    /// <summary>Total snapshots currently stored.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
