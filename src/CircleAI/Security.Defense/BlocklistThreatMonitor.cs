// BlocklistThreatMonitor.cs
//
// The default IThreatMonitor. Inherits CircleAIComponentBase so the signal STREAM
// gets the SDK's standard OTel activity + audit wrapper — but Evaluate() itself is
// a plain synchronous method (no per-call activity/audit) so the connection hot
// path stays cheap on low-end Android. Detection order:
//   1. allowlist short-circuit
//   2. known-bad indicator match  → High, escalating to Critical (C2) on repeat
//   3. connection-rate anomaly    → Medium (scan / flood)

using System.Threading.Channels;
using CircleAI.Core.Components;
using CircleAI.Core.Validation;
using Microsoft.Extensions.Logging;

namespace CircleAI.Security.Defense;

/// <summary>
/// Evaluates network observations against the bundled indicator index and anomaly
/// heuristics, emitting <see cref="ThreatSignal"/>s and broadcasting them to a
/// stream for sinks and dashboards.
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.WireProven,
    Notes = "Deterministic evaluation: O(1) IOC lookup + bounded sliding-window anomaly + beacon-repeat " +
            "escalation. Evaluate() is synchronous and allocation-light for low-end Android; the signal " +
            "stream uses an in-process Channel (single-process, not multi-replica).")]
public sealed class BlocklistThreatMonitor : CircleAIComponentBase, IThreatMonitor
{
    private readonly IIndicatorSource _indicators;
    private readonly DefenseOptions _options;
    private readonly ConnectionRateAnomalyDetector _anomaly;
    private readonly BeaconTracker _beacons;

    private readonly Channel<ThreatSignal> _signals =
        Channel.CreateUnbounded<ThreatSignal>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    /// <inheritdoc/>
    public override string ComponentName => "BlocklistThreatMonitor";

    /// <summary>Constructs the monitor over an indicator source and options.</summary>
    public BlocklistThreatMonitor(
        IIndicatorSource indicators,
        DefenseOptions? options = null,
        ILogger<BlocklistThreatMonitor>? logger = null)
        : base(logger)
    {
        _indicators = indicators ?? throw new ArgumentNullException(nameof(indicators));
        _options = options ?? new DefenseOptions();
        _anomaly = new ConnectionRateAnomalyDetector(_options);
        _beacons = new BeaconTracker(_options);
    }

    /// <inheritdoc/>
    public ThreatSignal? Evaluate(NetworkObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (IsAllowed(observation))
            return null;

        ThreatSignal? signal = Classify(observation);
        if (signal is null || signal.Severity < _options.MinReportSeverity)
            return null;

        // Non-blocking publish to the stream. Unbounded channel never rejects.
        _signals.Writer.TryWrite(signal);
        return signal;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ThreatSignal> StreamSignalsAsync(CancellationToken ct = default) =>
        RunStreamAsync<ThreatSignal>(
            "StreamSignalsAsync",
            c => _signals.Reader.ReadAllAsync(c),
            ct);

    private ThreatSignal? Classify(NetworkObservation observation)
    {
        IndicatorMatch? match = _indicators.Match(observation.RemoteAddress, observation.Host);
        if (match is { } hit)
        {
            int repeats = _beacons.Record(hit.Indicator);
            bool beaconing = repeats >= _options.BeaconRepeatThreshold;

            ThreatCategory category = beaconing
                ? ThreatCategory.CommandAndControl
                : hit.Kind == IndicatorKind.Domain
                    ? ThreatCategory.KnownMalwareHost
                    : ThreatCategory.MaliciousEndpoint;

            ThreatSeverity severity = beaconing ? ThreatSeverity.Critical : ThreatSeverity.High;
            double confidence = beaconing ? 0.98 : 0.90;

            var tags = new List<string> { hit.Reason, hit.Kind.ToString().ToLowerInvariant() };
            if (beaconing) tags.Add($"beacon-x{repeats}");

            string description = beaconing
                ? $"Repeated contact ({repeats}x) with known-bad indicator '{hit.Indicator}' — possible C2 beaconing."
                : $"Contact with known-bad indicator '{hit.Indicator}' ({hit.Reason}).";

            return ThreatSignal.Create(
                category, severity, confidence, hit.Indicator, description,
                observation.Direction, tags, observation);
        }

        if (_options.EnableAnomalyDetection && observation.Direction == ThreatDirection.Outbound)
            return _anomaly.Observe(observation);

        return null;
    }

    private bool IsAllowed(NetworkObservation observation)
    {
        if (observation.Host is { Length: > 0 } host
            && _options.AllowedHosts.Contains(host.TrimEnd('.')))
            return true;

        if (observation.RemoteAddress is { } remote
            && _options.AllowedAddresses.Contains(remote.ToString()))
            return true;

        return false;
    }
}
