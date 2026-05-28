// ModelDelta.cs
//
// One participant's contribution to a federation round. The payload encoding
// is engine-specific (typically a packed float[] of weight deltas); the
// signature is produced by the contributor's UhidKeyRing so the aggregator
// can verify provenance before averaging.
//
// Crucially: NO raw training data leaves the device — only the delta.

namespace Circle.AI.Federation;

/// <summary>
/// One participant's signed contribution to a federation round.
/// </summary>
/// <param name="Id">Unique delta identifier.</param>
/// <param name="RoundId">Identifier of the <see cref="FederationRound"/> this delta belongs to.</param>
/// <param name="ContributorUhid">
/// Pseudonymous UHID (hashed). NEVER raw PII — always a one-way hash so the
/// aggregator can deduplicate without learning the user's identity.
/// </param>
/// <param name="ModelId">Model the delta applies to.</param>
/// <param name="FromVersion">Base model version the participant trained on.</param>
/// <param name="DeltaPayload">
/// Opaque byte blob carrying the weight deltas. The reference aggregator
/// interprets this as a little-endian IEEE 754 <c>float[]</c>; engine-specific
/// implementations may use any encoding as long as the aggregator agrees.
/// </param>
/// <param name="SampleCount">
/// Number of local training samples the participant used to produce the delta.
/// Used by federated averaging as the weighting factor.
/// </param>
/// <param name="Signature">
/// ECDSA-SHA256 signature over the delta payload produced by the contributor's
/// <c>UhidKeyRing</c>. The aggregator verifies this via a caller-supplied
/// validator delegate so this package does not need to depend on the key ring.
/// </param>
/// <param name="SubmittedAt">UTC timestamp of submission.</param>
public sealed record ModelDelta(
    Guid Id,
    Guid RoundId,
    string ContributorUhid,
    string ModelId,
    string FromVersion,
    byte[] DeltaPayload,
    int SampleCount,
    byte[] Signature,
    DateTimeOffset SubmittedAt);
