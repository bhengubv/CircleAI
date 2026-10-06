// SchedulingHint.cs
//
// Advisory data that the Circle AI reasoning layer can attach to a SyncDelta to
// tell the underlying Aether transport layer *when* and *to whom* the delta
// should be delivered. The transport layer is free to ignore these hints; they
// are never a correctness constraint, only a performance advisory.

namespace CircleAI.Networking;

/// <summary>
/// Advisory scheduling information attached to a <see cref="SyncDelta"/> by the
/// Circle AI layer. The Aether transport is free to disregard these hints, but
/// honouring them minimises unnecessary wakeups and battery drain on constrained
/// devices.
/// </summary>
/// <param name="PreferredPeerIds">
/// Device IDs that are strongly preferred as the first delivery targets.
/// Typically populated with IDs of recently-active or nearby peers, derived
/// from affect state or episodic memory. Empty means "no preference".
/// </param>
/// <param name="SuggestedWindowUtc">
/// The earliest UTC timestamp at which the transport should attempt delivery.
/// When <c>null</c>, the delta should be forwarded immediately. Used to batch
/// non-urgent memory syncs outside peak interaction windows.
/// </param>
/// <param name="ConfidenceScore">
/// How confident the AI layer is that these hints are accurate, in the range
/// [0.0, 1.0]. A score below 0.5 is a weak advisory; the transport should
/// apply normal routing. A score above 0.8 is a strong advisory.
/// </param>
public sealed record SchedulingHint(
    IReadOnlyList<string> PreferredPeerIds,
    DateTimeOffset? SuggestedWindowUtc,
    float ConfidenceScore);
