// IFederationParticipant.cs
//
// Implemented by every device that wants to take part in a federation round.
// The participant trains locally, produces a signed delta, and later applies
// the aggregated model published by the aggregator.

namespace CircleAI.Federation;

/// <summary>
/// Contract for a device that contributes to federation rounds. The
/// participant is responsible for local training, producing the signed
/// delta, and accepting an aggregated model when the aggregator publishes one.
/// </summary>
public interface IFederationParticipant
{
    /// <summary>
    /// Trains locally against the participant's private data and returns the
    /// resulting signed <see cref="ModelDelta"/>. Implementations MUST NOT
    /// transmit raw training data — only the delta payload leaves the device.
    /// </summary>
    /// <param name="round">The round to produce a delta for.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    Task<ModelDelta> ProduceDeltaAsync(FederationRound round, CancellationToken ct = default);

    /// <summary>
    /// Applies an aggregated model published by the aggregator and reports
    /// whether the application succeeded (e.g. checksum validation passed,
    /// the engine accepted the new weights).
    /// </summary>
    /// <param name="modelId">Model the update applies to.</param>
    /// <param name="newVersion">Semver of the new aggregated model.</param>
    /// <param name="aggregatedPayload">Opaque aggregated weights blob.</param>
    /// <param name="ct">Cooperative cancellation.</param>
    Task<bool> ApplyAggregatedModelAsync(
        string modelId,
        string newVersion,
        byte[] aggregatedPayload,
        CancellationToken ct = default);
}
