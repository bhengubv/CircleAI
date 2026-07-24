// AlwaysOnDefenseSentinel.cs
//
// The autonomic posture: the "always-on" part of the immune system. It is NOT a
// user-launched feature — a host starts it once at application boot and it runs for
// the lifetime of the process, pumping every observation from the platform feed
// through the monitor and routing detected signals to the sink. This is the
// pre-launch baseline the brief calls for: a user is never in the wild unprotected.

using CircleAI.Core.Components;
using CircleAI.Core.Validation;
using Microsoft.Extensions.Logging;

namespace CircleAI.Security.Defense;

/// <summary>
/// Lifecycle of the always-on defensive posture. Start once at boot; it is
/// autonomic thereafter. A host may wrap this in its own hosted-service /
/// foreground-service shell.
/// </summary>
public interface IAutonomicDefense
{
    /// <summary>Whether the posture is currently running.</summary>
    bool IsActive { get; }

    /// <summary>Starts the autonomic monitoring loop. Idempotent.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops the loop and releases resources. Idempotent.</summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Drives the defensive monitor from a platform observation feed and routes every
/// detected <see cref="ThreatSignal"/> to a sink. Resilient by design: a failure
/// evaluating one observation, or in one sink, is logged and the loop continues.
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.Reference,
    Notes = "Supervisor loop verified in-process against a synthetic INetworkObservationFeed. Real " +
            "always-on operation needs a host-provided feed (e.g. Android VpnService connection metadata " +
            "or AetherNet connection events). Single-process.")]
public sealed class AlwaysOnDefenseSentinel : CircleAIComponentBase, IAutonomicDefense
{
    private readonly IThreatMonitor _monitor;
    private readonly INetworkObservationFeed _feed;
    private readonly IThreatSink _sink;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _active;

    /// <inheritdoc/>
    public override string ComponentName => "AlwaysOnDefenseSentinel";

    /// <inheritdoc/>
    public bool IsActive => _active;

    /// <summary>Constructs the sentinel over a monitor, a feed, and an optional sink.</summary>
    public AlwaysOnDefenseSentinel(
        IThreatMonitor monitor,
        INetworkObservationFeed feed,
        IThreatSink? sink = null,
        DefenseOptions? options = null,
        ILogger<AlwaysOnDefenseSentinel>? logger = null)
        : base(logger)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _sink = sink ?? NullThreatSink.Instance;
        _ = options; // reserved for future loop tuning; kept for a stable ctor shape
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_active) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken loopToken = _cts.Token; // capture before StopAsync can null _cts
        _active = true;
        Logger.LogInformation("Autonomic defence started (feed '{Feed}').", _feed.SourceId);
        _loop = Task.Run(() => RunLoopAsync(loopToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_active) return;
        _active = false;

        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Logger.LogInformation("Autonomic defence stopped.");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (NetworkObservation observation in _feed.ObserveAsync(ct).ConfigureAwait(false))
            {
                ThreatSignal? signal;
                try
                {
                    signal = _monitor.Evaluate(observation);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Monitor evaluation failed for an observation; continuing.");
                    continue;
                }

                if (signal is null)
                    continue;

                try
                {
                    await _sink.HandleAsync(signal, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Threat sink failed for signal '{Indicator}'; continuing.", signal.Indicator);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The feed itself faulted. Surface it; the host decides whether to restart.
            Logger.LogError(ex, "Observation feed '{Feed}' faulted; autonomic loop ending.", _feed.SourceId);
        }
    }
}
