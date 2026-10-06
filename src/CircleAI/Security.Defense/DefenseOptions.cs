// DefenseOptions.cs
//
// Tunables for the defensive immune system. Defaults are chosen for a low-end
// Android device running always-on: cheap windows, bounded memory, and severity
// floors that keep noise off the SOS channel while still feeding the watchdog.

namespace CircleAI.Security.Defense;

/// <summary>
/// Configuration for the defensive monitor, anomaly detector, and routing floors.
/// </summary>
public sealed class DefenseOptions
{
    /// <summary>
    /// Minimum severity for <see cref="IThreatMonitor.Evaluate"/> to emit a signal.
    /// Observations that classify below this are treated as clean. Default:
    /// <see cref="ThreatSeverity.Low"/>.
    /// </summary>
    public ThreatSeverity MinReportSeverity { get; set; } = ThreatSeverity.Low;

    /// <summary>
    /// Minimum severity the <see cref="WatchdogThreatSink"/> forwards into the
    /// existing <c>ISecurityWatchdog</c>. Default: <see cref="ThreatSeverity.High"/>
    /// (known-bad contact).
    /// </summary>
    public ThreatSeverity WatchdogSeverityFloor { get; set; } = ThreatSeverity.High;

    /// <summary>
    /// Minimum severity that triggers Panik/Nope SOS escalation. Default:
    /// <see cref="ThreatSeverity.Critical"/> (e.g. confirmed C2 beaconing) so the
    /// SOS channel stays quiet until the device is genuinely compromised.
    /// </summary>
    public ThreatSeverity SosSeverityFloor { get; set; } = ThreatSeverity.Critical;

    /// <summary>Whether the connection-rate anomaly heuristics run. Default: <c>true</c>.</summary>
    public bool EnableAnomalyDetection { get; set; } = true;

    /// <summary>Sliding window for connection-rate anomaly detection. Default: 10 s.</summary>
    public TimeSpan AnomalyWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Distinct outbound destinations within <see cref="AnomalyWindow"/> that trip a
    /// scan/sweep signal. Default: 20.
    /// </summary>
    public int DistinctDestinationScanThreshold { get; set; } = 20;

    /// <summary>
    /// Total outbound connections within <see cref="AnomalyWindow"/> that trip a
    /// flood signal. Default: 100.
    /// </summary>
    public int ConnectionFloodThreshold { get; set; } = 100;

    /// <summary>
    /// Upper bound on tracked connection/indicator state to cap memory on low-end
    /// devices. Oldest entries are evicted first. Default: 512.
    /// </summary>
    public int MaxTrackedConnections { get; set; } = 512;

    /// <summary>
    /// Repeated contacts with the SAME known-bad indicator within
    /// <see cref="BeaconWindow"/> that escalate a High hit to a Critical
    /// command-and-control beaconing signal. Default: 3.
    /// </summary>
    public int BeaconRepeatThreshold { get; set; } = 3;

    /// <summary>Window over which repeated known-bad contacts count as beaconing. Default: 5 min.</summary>
    public TimeSpan BeaconWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hosts to never flag (exact, case-insensitive). Populate with first-party
    /// endpoints if a bundled feed is over-broad.
    /// </summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remote addresses to never flag (exact string form).</summary>
    public ISet<string> AllowedAddresses { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Advisory cadence for refreshing indicators from a feed. The monitor works
    /// fully offline; this only hints how often a host <em>may</em> call
    /// <see cref="IIndicatorSource.RefreshFromAsync"/> when a feed is reachable.
    /// Default: 12 h.
    /// </summary>
    public TimeSpan RefreshHint { get; set; } = TimeSpan.FromHours(12);
}
