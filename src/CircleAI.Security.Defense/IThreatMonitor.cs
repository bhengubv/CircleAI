// IThreatMonitor.cs
//
// The central contract of the defensive immune system's data plane.
//
// Evaluate() is the HOT PATH: it is called once per network observation, must be
// fast, synchronous and allocation-light (in-memory O(1) indicator lookup +
// bounded sliding-window anomaly check), because on a low-end phone it may run on
// every outbound connection. It returns a ThreatSignal when the observation is a
// threat worth reporting, or null when it is clean / below the reporting floor.
//
// StreamSignalsAsync() is the observation plane: every reported signal is also
// broadcast here for dashboards, loggers, and the SOS/watchdog sinks.

namespace CircleAI.Security.Defense;

/// <summary>
/// On-device network threat monitor. Evaluates individual
/// <see cref="NetworkObservation"/> instances against known-bad indicators and
/// anomaly heuristics, and streams the resulting <see cref="ThreatSignal"/>s.
/// </summary>
public interface IThreatMonitor
{
    /// <summary>
    /// Evaluates a single observation. Fast, synchronous, safe to call on the
    /// connection hot path. Returns the detected <see cref="ThreatSignal"/>, or
    /// <c>null</c> when the observation is clean or below the reporting floor.
    /// A returned signal has also been published to <see cref="StreamSignalsAsync"/>.
    /// </summary>
    ThreatSignal? Evaluate(NetworkObservation observation);

    /// <summary>
    /// Streams every reported <see cref="ThreatSignal"/> as it is detected.
    /// Completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<ThreatSignal> StreamSignalsAsync(CancellationToken ct = default);
}
