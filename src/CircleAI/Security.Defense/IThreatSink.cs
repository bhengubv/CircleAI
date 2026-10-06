// IThreatSink.cs
//
// Where confirmed ThreatSignals go. Decouples the always-on posture from what
// happens next: log it, forward it to the ISecurityWatchdog (WatchdogThreatSink),
// raise SOS (SosThreatSink), or fan out to several via CompositeThreatSink.

using Microsoft.Extensions.Logging;

namespace CircleAI.Security.Defense;

/// <summary>Receives every reported <see cref="ThreatSignal"/> for downstream action.</summary>
public interface IThreatSink
{
    /// <summary>Handles a reported threat signal.</summary>
    Task HandleAsync(ThreatSignal signal, CancellationToken ct = default);
}

/// <summary>A sink that does nothing. Useful as a default and in tests.</summary>
public sealed class NullThreatSink : IThreatSink
{
    /// <summary>Shared instance.</summary>
    public static NullThreatSink Instance { get; } = new();

    /// <inheritdoc/>
    public Task HandleAsync(ThreatSignal signal, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Adapts a delegate into an <see cref="IThreatSink"/>.</summary>
public sealed class DelegateThreatSink : IThreatSink
{
    private readonly Func<ThreatSignal, CancellationToken, Task> _handler;

    /// <summary>Wraps <paramref name="handler"/>.</summary>
    public DelegateThreatSink(Func<ThreatSignal, CancellationToken, Task> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <inheritdoc/>
    public Task HandleAsync(ThreatSignal signal, CancellationToken ct = default) => _handler(signal, ct);
}

/// <summary>
/// Fans a signal out to several sinks. One sink throwing does not stop the others:
/// the failure is logged (never silently swallowed) and delivery continues, so a
/// broken SOS path can never suppress the watchdog path. Cancellation propagates.
/// </summary>
public sealed class CompositeThreatSink : IThreatSink
{
    private readonly IReadOnlyList<IThreatSink> _sinks;
    private readonly ILogger? _logger;

    /// <summary>Composes the given sinks.</summary>
    public CompositeThreatSink(IEnumerable<IThreatSink> sinks, ILogger<CompositeThreatSink>? logger = null)
    {
        _sinks = sinks?.ToList() ?? throw new ArgumentNullException(nameof(sinks));
        _logger = logger;
    }

    /// <summary>Composes the given sinks.</summary>
    public CompositeThreatSink(params IThreatSink[] sinks)
        : this((IEnumerable<IThreatSink>)sinks)
    {
    }

    /// <inheritdoc/>
    public async Task HandleAsync(ThreatSignal signal, CancellationToken ct = default)
    {
        foreach (IThreatSink sink in _sinks)
        {
            try
            {
                await sink.HandleAsync(signal, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Threat sink {Sink} failed for signal {Indicator}; continuing with remaining sinks.",
                    sink.GetType().Name, signal.Indicator);
            }
        }
    }
}
