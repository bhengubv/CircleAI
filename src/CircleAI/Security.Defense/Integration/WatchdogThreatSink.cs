// WatchdogThreatSink.cs
//
// The complement bridge. CircleAI.Security already owns a local runtime immune
// system (ISecurityWatchdog + graduated SecurityResponse: key rotation, mesh
// isolation, state rollback). Rather than duplicate that response policy for the
// network surface, this sink translates a network ThreatSignal into an
// AnomalySignal and hands it to the existing watchdog — so one policy covers both
// the local runtime and the wire.
//
// This is the ONLY place that touches CircleAI.Security, and it only CONSUMES it
// (project reference); no files inside CircleAI.Security are modified. AnomalySignal,
// ThreatVector and ISecurityWatchdog resolve from the enclosing CircleAI.Security
// namespace; the using is kept explicit for clarity.

using CircleAI.Security;
using Microsoft.Extensions.Logging;

namespace CircleAI.Security.Defense;

/// <summary>
/// Forwards network <see cref="ThreatSignal"/>s at or above
/// <see cref="DefenseOptions.WatchdogSeverityFloor"/> into the CircleAI.Security
/// <see cref="ISecurityWatchdog"/> as <see cref="AnomalySignal"/>s.
/// </summary>
public sealed class WatchdogThreatSink : IThreatSink
{
    private readonly ISecurityWatchdog _watchdog;
    private readonly DefenseOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Constructs the bridge over an existing security watchdog.</summary>
    public WatchdogThreatSink(
        ISecurityWatchdog watchdog,
        DefenseOptions? options = null,
        ILogger<WatchdogThreatSink>? logger = null)
    {
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _options = options ?? new DefenseOptions();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task HandleAsync(ThreatSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Severity < _options.WatchdogSeverityFloor)
            return;

        var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["indicator"] = signal.Indicator,
            ["category"] = signal.Category.ToString(),
            ["severity"] = signal.Severity.ToString(),
            ["direction"] = signal.Direction.ToString(),
        };
        if (signal.Observation?.Host is { Length: > 0 } host)
            evidence["host"] = host;
        if (signal.Observation?.RemoteAddress is { } remote)
            evidence["remote"] = remote.ToString();
        if (signal.Observation?.AppHint is { Length: > 0 } app)
            evidence["app"] = app;

        ThreatVector vector = MapVector(signal.Category);
        AnomalySignal anomaly = AnomalySignal.Create(
            vector,
            signal.Confidence,
            affectedModule: "CircleAI.Security.Defense",
            description: signal.Description,
            evidence: evidence);

        _logger?.LogWarning(
            "Forwarding {Category} network threat on '{Indicator}' to security watchdog as {Vector}.",
            signal.Category, signal.Indicator, vector);

        // Fire into the existing immune system; the watchdog decides the response.
        _ = await _watchdog.OnAnomalyDetectedAsync(anomaly, checkpoint: null, ct: ct).ConfigureAwait(false);
    }

    // Network threats are, from the local runtime's perspective, an external node
    // attempting to reach into / exfiltrate from this process — closest existing
    // vector is NetworkPivot. Rate/scan anomalies have no precise vector → Unknown.
    private static ThreatVector MapVector(ThreatCategory category) => category switch
    {
        ThreatCategory.CommandAndControl => ThreatVector.NetworkPivot,
        ThreatCategory.DataExfiltration => ThreatVector.NetworkPivot,
        ThreatCategory.MaliciousEndpoint => ThreatVector.NetworkPivot,
        ThreatCategory.KnownMalwareHost => ThreatVector.NetworkPivot,
        ThreatCategory.Phishing => ThreatVector.NetworkPivot,
        _ => ThreatVector.Unknown,
    };
}
