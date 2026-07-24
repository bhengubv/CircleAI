// ThreatSignal.cs
//
// The network-facing threat model for the defensive immune system.
//
// Deliberately distinct from the two models already in CircleAI.Security:
//   * PeerSecurityEvent (peer-trust vocabulary — who to route around on the mesh)
//   * AnomalySignal     (local runtime anomalies — memory / control-flow / biometric)
// ThreatSignal describes something the DEVICE observed on the wire: a contact with
// a known-bad endpoint, or an anomalous connection pattern. The optional
// Integration\WatchdogThreatSink bridges a ThreatSignal into an AnomalySignal so the
// existing ISecurityWatchdog response policy covers this surface too.

namespace CircleAI.Security.Defense;

/// <summary>
/// Severity of a network threat. Ordered so <see cref="Info"/> is benign and
/// <see cref="Critical"/> is worst; mirrors <c>PeerThreatLevel</c> for easy mapping.
/// </summary>
public enum ThreatSeverity
{
    /// <summary>Informational — recorded, no action warranted.</summary>
    Info = 0,

    /// <summary>Low — monitor only.</summary>
    Low = 1,

    /// <summary>Medium — notable anomaly (scan/flood pattern).</summary>
    Medium = 2,

    /// <summary>High — contact with a known-bad indicator.</summary>
    High = 3,

    /// <summary>Critical — active/confirmed compromise (e.g. C2 beaconing) → SOS-eligible.</summary>
    Critical = 4,
}

/// <summary>
/// Classification of a locally-observed network threat.
/// </summary>
public enum ThreatCategory
{
    /// <summary>Did not map to a specific category.</summary>
    Unclassified = 0,

    /// <summary>Contact with an endpoint on the known-bad IP/CIDR list.</summary>
    MaliciousEndpoint,

    /// <summary>Contact with a host on the known-bad domain list.</summary>
    KnownMalwareHost,

    /// <summary>Repeated contact with a known-bad indicator — command-and-control beaconing.</summary>
    CommandAndControl,

    /// <summary>Contact with a known phishing host.</summary>
    Phishing,

    /// <summary>Outbound volume/destination pattern consistent with data exfiltration.</summary>
    DataExfiltration,

    /// <summary>Outbound fan-out to many distinct destinations — scan/sweep pattern.</summary>
    PortScan,

    /// <summary>High outbound connection rate — flood / DoS-source pattern.</summary>
    ConnectionFlood,

    /// <summary>Anomalous DNS lookup behaviour (e.g. DGA-like burst).</summary>
    DnsAnomaly,
}

/// <summary>
/// An immutable record describing a network threat the device observed.
/// Produced by <see cref="IThreatMonitor.Evaluate"/> and routed to every
/// <see cref="IThreatSink"/>.
/// </summary>
/// <param name="Id">Unique identifier for this signal instance.</param>
/// <param name="Category">Classification of the threat.</param>
/// <param name="Severity">Assessed severity.</param>
/// <param name="Confidence">Confidence this is a genuine threat, in [0.0, 1.0].</param>
/// <param name="Indicator">The matched indicator or the offending endpoint (IP/host/pattern).</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Direction">Direction of the observed traffic.</param>
/// <param name="Tags">Machine-readable indicator tags (e.g. "known-bad-ip", "beacon-x4").</param>
/// <param name="Observation">The originating observation, when available.</param>
/// <param name="DetectedAt">UTC timestamp of detection.</param>
public sealed record ThreatSignal(
    Guid Id,
    ThreatCategory Category,
    ThreatSeverity Severity,
    double Confidence,
    string Indicator,
    string Description,
    ThreatDirection Direction,
    IReadOnlyList<string> Tags,
    NetworkObservation? Observation,
    DateTimeOffset DetectedAt)
{
    /// <summary>
    /// Creates a <see cref="ThreatSignal"/> with a fresh <see cref="Guid"/>,
    /// clamped confidence, and the current UTC time.
    /// </summary>
    public static ThreatSignal Create(
        ThreatCategory category,
        ThreatSeverity severity,
        double confidence,
        string indicator,
        string description,
        ThreatDirection direction,
        IReadOnlyList<string>? tags = null,
        NetworkObservation? observation = null) =>
        new(
            Guid.NewGuid(),
            category,
            severity,
            Math.Clamp(confidence, 0.0, 1.0),
            indicator,
            description,
            direction,
            tags ?? Array.Empty<string>(),
            observation,
            DateTimeOffset.UtcNow);
}
