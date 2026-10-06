// AnomalySignal.cs
//
// Carries the details of a locally-detected runtime anomaly from the
// detection site to the ISecurityWatchdog handler.
//
// The signal is IMMUTABLE — detection sites create it and hand it off.
// The watchdog (and any ops-security agent) reads it and decides the response.

using System.Text.Json.Serialization;

namespace CircleAI.Security;

/// <summary>
/// An immutable record describing a locally-detected runtime anomaly.
/// Created at the detection site (e.g. the companion pipeline, the biometric
/// verifier, or an agent patch gate) and consumed by
/// <see cref="ISecurityWatchdog.OnAnomalyDetectedAsync"/>.
/// </summary>
/// <param name="Id">Unique identifier for this signal instance.</param>
/// <param name="Vector">Classification of the detected threat.</param>
/// <param name="Confidence">
/// Confidence that this is a genuine threat, in [0.0, 1.0].
/// 1.0 = definitive; 0.0 = speculative.
/// </param>
/// <param name="AffectedModule">
/// The module or subsystem where the anomaly was detected
/// (e.g. <c>"CircleAI.Companion"</c>, <c>"CircleAI.Identity"</c>).
/// </param>
/// <param name="Description">Human-readable description of the anomaly.</param>
/// <param name="Evidence">
/// Optional structured evidence attached by the detection site.
/// Keys are evidence labels; values are serialised data or hashes.
/// </param>
/// <param name="DetectedAt">UTC timestamp of detection.</param>
public sealed record AnomalySignal(
    Guid Id,
    ThreatVector Vector,
    double Confidence,
    string AffectedModule,
    string Description,
    [property: JsonConverter(typeof(RedactedEvidenceJsonConverter))]
    IReadOnlyDictionary<string, string> Evidence,
    DateTimeOffset DetectedAt)
{
    /// <summary>
    /// Creates an <see cref="AnomalySignal"/> with a new <see cref="Guid"/>
    /// and the current UTC time.
    /// </summary>
    public static AnomalySignal Create(
        ThreatVector vector,
        double confidence,
        string affectedModule,
        string description,
        IReadOnlyDictionary<string, string>? evidence = null) =>
        new(
            Guid.NewGuid(),
            vector,
            Math.Clamp(confidence, 0.0, 1.0),
            affectedModule,
            description,
            evidence ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);
}
