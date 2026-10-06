// DefenseModule.cs
//
// One-call wiring of the whole defensive stack with NO DI-container dependency
// (keeps the library lean and de-Googled — a host can still register the pieces in
// its own container if it prefers). Loads the bundled offline blocklist, builds the
// monitor, and returns a started-on-demand sentinel bound to the host's feed + sink.

using Microsoft.Extensions.Logging;

namespace CircleAI.Security.Defense;

/// <summary>
/// Convenience assembly of the defensive immune system: bundled indicators →
/// monitor → always-on sentinel. Construct via <see cref="CreateAsync"/>, then
/// <see cref="StartAsync"/> at application boot.
/// </summary>
public sealed class DefenseModule
{
    /// <summary>The indicator index (bundled blocklist; refreshable at runtime).</summary>
    public IIndicatorSource Indicators { get; }

    /// <summary>The network threat monitor.</summary>
    public IThreatMonitor Monitor { get; }

    /// <summary>The always-on autonomic posture.</summary>
    public IAutonomicDefense Sentinel { get; }

    /// <summary>The effective options.</summary>
    public DefenseOptions Options { get; }

    private DefenseModule(
        IIndicatorSource indicators,
        IThreatMonitor monitor,
        IAutonomicDefense sentinel,
        DefenseOptions options)
    {
        Indicators = indicators;
        Monitor = monitor;
        Sentinel = sentinel;
        Options = options;
    }

    /// <summary>
    /// Builds a ready-to-start defensive stack. <paramref name="feed"/> is the
    /// host's platform observation source; <paramref name="sink"/> is where signals
    /// go (wrap several with <see cref="CompositeThreatSink"/> — e.g. a
    /// <see cref="WatchdogThreatSink"/> plus a <see cref="SosThreatSink"/>).
    /// </summary>
    public static async Task<DefenseModule> CreateAsync(
        INetworkObservationFeed feed,
        IThreatSink? sink = null,
        DefenseOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        options ??= new DefenseOptions();

        BlocklistIndicatorSource indicators = await BlocklistIndicatorSource
            .CreateFromBundledAsync(ct)
            .ConfigureAwait(false);

        var monitor = new BlocklistThreatMonitor(
            indicators, options, loggerFactory?.CreateLogger<BlocklistThreatMonitor>());

        var sentinel = new AlwaysOnDefenseSentinel(
            monitor, feed, sink, options, loggerFactory?.CreateLogger<AlwaysOnDefenseSentinel>());

        return new DefenseModule(indicators, monitor, sentinel, options);
    }

    /// <summary>Starts the always-on posture. Call once at boot.</summary>
    public Task StartAsync(CancellationToken ct = default) => Sentinel.StartAsync(ct);

    /// <summary>Stops the always-on posture.</summary>
    public Task StopAsync(CancellationToken ct = default) => Sentinel.StopAsync(ct);
}
