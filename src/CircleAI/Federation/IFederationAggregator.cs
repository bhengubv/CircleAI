// IFederationAggregator.cs
//
// The coordinator side of federated learning. Owns the round lifecycle:
// opens a round, accepts signed deltas, commits when MinParticipants is
// reached, and emits the aggregated payload.

namespace CircleAI.Federation;

/// <summary>
/// Coordinator for federation rounds. Implementations may store deltas in
/// memory (tests, edge), in SQLite (on-device), or in a distributed mesh
/// (production over Aether). The contract is the same.
/// </summary>
public interface IFederationAggregator
{
    /// <summary>
    /// Opens a new round for <paramref name="modelId"/> moving from
    /// <paramref name="fromVersion"/> to <paramref name="toVersion"/>.
    /// </summary>
    /// <param name="modelId">Canonical model name.</param>
    /// <param name="fromVersion">Base model version.</param>
    /// <param name="toVersion">Target model version after aggregation.</param>
    /// <param name="minParticipants">Minimum deltas required before committing.</param>
    /// <param name="maxParticipants">Hard upper bound on accepted deltas.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    Task<FederationRound> OpenRoundAsync(
        string modelId,
        string fromVersion,
        string toVersion,
        int minParticipants,
        int maxParticipants,
        CancellationToken ct = default);

    /// <summary>
    /// Submits a signed delta to its associated round. Implementations MUST
    /// reject deltas whose <see cref="ModelDelta.RoundId"/> does not match an
    /// open round, and MUST raise <see cref="InvalidOperationException"/> when
    /// the round has already reached <see cref="FederationRound.MaxParticipants"/>.
    /// </summary>
    /// <param name="delta">The signed delta to submit.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    Task SubmitDeltaAsync(ModelDelta delta, CancellationToken ct = default);

    /// <summary>
    /// Attempts to commit the round. Returns the aggregated payload when
    /// <see cref="FederationRound.MinParticipants"/> valid deltas have been
    /// collected; returns <c>null</c> otherwise. On success the round flips
    /// to <see cref="RoundStatus.Committed"/>.
    /// </summary>
    /// <param name="roundId">The round to commit.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    Task<byte[]?> TryCommitAsync(Guid roundId, CancellationToken ct = default);

    /// <summary>
    /// Returns the current <see cref="FederationRound"/> snapshot. Throws
    /// <see cref="KeyNotFoundException"/> if the round is unknown.
    /// </summary>
    /// <param name="roundId">The round identifier.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    Task<FederationRound> GetRoundAsync(Guid roundId, CancellationToken ct = default);
}
