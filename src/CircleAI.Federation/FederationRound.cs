// FederationRound.cs
//
// One coordinated round of federated learning. Participants pull the FromVersion
// of the model, train locally, and submit a ModelDelta. When MinParticipants
// deltas arrive, the aggregator commits an averaged update and emits the
// ToVersion of the model.

namespace CircleAI.Federation;

/// <summary>
/// Lifecycle state of a <see cref="FederationRound"/>.
/// </summary>
public enum RoundStatus
{
    /// <summary>Round is accepting deltas from participants.</summary>
    Open,

    /// <summary>Round has the minimum delta count and is averaging.</summary>
    Aggregating,

    /// <summary>Round committed an aggregated model; further deltas rejected.</summary>
    Committed,

    /// <summary>Round was abandoned (timeout, insufficient participants, etc.).</summary>
    Aborted,
}

/// <summary>
/// One coordinated round of federated learning, identified by
/// <see cref="Id"/> and bound to a specific model version transition
/// (<see cref="FromVersion"/> → <see cref="ToVersion"/>).
/// </summary>
/// <param name="Id">Unique round identifier.</param>
/// <param name="ModelId">Canonical model name shared by all participants.</param>
/// <param name="FromVersion">Semantic version of the base model participants train on.</param>
/// <param name="ToVersion">Semantic version the aggregated model will publish as.</param>
/// <param name="MinParticipants">
/// Minimum number of valid deltas required before the round may commit.
/// Below this threshold, <c>TryCommitAsync</c> returns <c>null</c>.
/// </param>
/// <param name="MaxParticipants">
/// Hard upper bound on accepted deltas. Submissions beyond this raise
/// <see cref="InvalidOperationException"/>.
/// </param>
/// <param name="CurrentParticipantCount">Number of deltas accepted so far.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="OpenedAt">UTC timestamp the round was opened.</param>
/// <param name="CommittedAt">
/// UTC timestamp the round was committed, or <c>null</c> if not yet committed.
/// </param>
public sealed record FederationRound(
    Guid Id,
    string ModelId,
    string FromVersion,
    string ToVersion,
    int MinParticipants,
    int MaxParticipants,
    int CurrentParticipantCount,
    RoundStatus Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? CommittedAt);
