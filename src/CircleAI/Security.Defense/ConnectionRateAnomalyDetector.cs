// ConnectionRateAnomalyDetector.cs
//
// Bounded, sliding-window heuristics that flag connection patterns the blocklist
// cannot catch: outbound fan-out (scan/sweep) and connection floods. Plus a
// BeaconTracker that escalates repeated contact with the SAME known-bad indicator
// into a command-and-control signal. Both cap their memory so an always-on monitor
// on a low-end phone never grows unbounded.

namespace CircleAI.Security.Defense;

/// <summary>
/// Detects outbound scan/flood patterns over a sliding time window. Internal —
/// consumed by <see cref="BlocklistThreatMonitor"/>.
/// </summary>
internal sealed class ConnectionRateAnomalyDetector
{
    private readonly DefenseOptions _options;
    private readonly object _gate = new();
    private readonly Queue<Entry> _events = new();
    private readonly Dictionary<string, int> _distinctCounts = new(StringComparer.Ordinal);

    public ConnectionRateAnomalyDetector(DefenseOptions options) => _options = options;

    private readonly record struct Entry(long Ticks, string Destination);

    /// <summary>
    /// Records an outbound observation and returns a scan/flood
    /// <see cref="ThreatSignal"/> if a threshold is crossed, else <c>null</c>.
    /// </summary>
    public ThreatSignal? Observe(NetworkObservation observation)
    {
        string destination = observation.RemoteAddress?.ToString() ?? observation.Host ?? "unknown";
        long now = DateTimeOffset.UtcNow.Ticks;
        long windowTicks = _options.AnomalyWindow.Ticks;

        int total;
        int distinct;
        lock (_gate)
        {
            _events.Enqueue(new Entry(now, destination));
            Increment(destination);

            // Evict events outside the window.
            while (_events.Count > 0 && now - _events.Peek().Ticks > windowTicks)
                Decrement(_events.Dequeue().Destination);

            // Hard cap on tracked events to bound memory.
            while (_events.Count > _options.MaxTrackedConnections)
                Decrement(_events.Dequeue().Destination);

            total = _events.Count;
            distinct = _distinctCounts.Count;
        }

        double windowSeconds = _options.AnomalyWindow.TotalSeconds;

        if (distinct >= _options.DistinctDestinationScanThreshold)
        {
            return ThreatSignal.Create(
                ThreatCategory.PortScan,
                ThreatSeverity.Medium,
                0.55,
                destination,
                $"Outbound fan-out to {distinct} distinct destinations within {windowSeconds:0}s — scan/sweep pattern.",
                ThreatDirection.Outbound,
                new List<string> { "scan-pattern", $"distinct-{distinct}" },
                observation);
        }

        if (total >= _options.ConnectionFloodThreshold)
        {
            return ThreatSignal.Create(
                ThreatCategory.ConnectionFlood,
                ThreatSeverity.Medium,
                0.50,
                destination,
                $"{total} outbound connections within {windowSeconds:0}s — flood / DoS-source pattern.",
                ThreatDirection.Outbound,
                new List<string> { "flood-pattern", $"count-{total}" },
                observation);
        }

        return null;
    }

    private void Increment(string destination) =>
        _distinctCounts[destination] = _distinctCounts.TryGetValue(destination, out int count) ? count + 1 : 1;

    private void Decrement(string destination)
    {
        if (!_distinctCounts.TryGetValue(destination, out int count)) return;
        if (count <= 1) _distinctCounts.Remove(destination);
        else _distinctCounts[destination] = count - 1;
    }
}

/// <summary>
/// Counts repeated contacts with each known-bad indicator over a sliding window so
/// a single hit (High) can escalate to beaconing (Critical). Internal — consumed by
/// <see cref="BlocklistThreatMonitor"/>.
/// </summary>
internal sealed class BeaconTracker
{
    private readonly DefenseOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<long>> _hits = new(StringComparer.OrdinalIgnoreCase);

    public BeaconTracker(DefenseOptions options) => _options = options;

    /// <summary>Records a hit on <paramref name="indicator"/>; returns hits within the beacon window.</summary>
    public int Record(string indicator)
    {
        long now = DateTimeOffset.UtcNow.Ticks;
        long windowTicks = _options.BeaconWindow.Ticks;

        lock (_gate)
        {
            if (!_hits.TryGetValue(indicator, out Queue<long>? timestamps))
            {
                timestamps = new Queue<long>();
                _hits[indicator] = timestamps;
            }

            timestamps.Enqueue(now);
            while (timestamps.Count > 0 && now - timestamps.Peek() > windowTicks)
                timestamps.Dequeue();

            // Bound the number of distinct indicators tracked.
            if (_hits.Count > _options.MaxTrackedConnections)
                PruneEmpty();

            return timestamps.Count;
        }
    }

    private void PruneEmpty()
    {
        List<string> empties = _hits.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
        foreach (string key in empties)
            _hits.Remove(key);
    }
}
