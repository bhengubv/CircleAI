// SosEscalation.cs
//
// The pairing seam with Panik/Nope SOS. The defensive monitor decides WHAT is a
// life-safety-grade compromise (default: Critical, e.g. confirmed C2 beaconing);
// the SOS product decides WHAT TO DO about it (silent alarm, trusted-contact
// notify, evidence capture). Keeping this an interface means the Defense library
// carries no dependency on the SOS app — the app implements ISosEscalation and
// registers a SosThreatSink.
//
// Namespace stays CircleAI.Security.Defense (folder is organisational only) so the
// SOS types sit alongside IThreatSink with no extra using.

using Microsoft.Extensions.Logging;

namespace CircleAI.Security.Defense;

/// <summary>
/// Implemented by a Panik/Nope SOS provider to receive life-safety-grade network
/// threat escalations from the defensive monitor.
/// </summary>
public interface ISosEscalation
{
    /// <summary>Escalates a critical threat to the SOS subsystem.</summary>
    Task EscalateAsync(ThreatSignal signal, CancellationToken ct = default);
}

/// <summary>An escalation target that does nothing. Default when no SOS is wired.</summary>
public sealed class NullSosEscalation : ISosEscalation
{
    /// <summary>Shared instance.</summary>
    public static NullSosEscalation Instance { get; } = new();

    /// <inheritdoc/>
    public Task EscalateAsync(ThreatSignal signal, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Adapts a delegate into an <see cref="ISosEscalation"/>.</summary>
public sealed class DelegateSosEscalation : ISosEscalation
{
    private readonly Func<ThreatSignal, CancellationToken, Task> _handler;

    /// <summary>Wraps <paramref name="handler"/>.</summary>
    public DelegateSosEscalation(Func<ThreatSignal, CancellationToken, Task> handler) =>
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <inheritdoc/>
    public Task EscalateAsync(ThreatSignal signal, CancellationToken ct = default) => _handler(signal, ct);
}

/// <summary>
/// A <see cref="IThreatSink"/> that forwards signals at or above
/// <see cref="DefenseOptions.SosSeverityFloor"/> to an <see cref="ISosEscalation"/>.
/// Register this in the sentinel's sink set to pair defence with Panik/Nope SOS.
/// </summary>
public sealed class SosThreatSink : IThreatSink
{
    private readonly ISosEscalation _sos;
    private readonly DefenseOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Constructs the SOS sink over an escalation target.</summary>
    public SosThreatSink(ISosEscalation sos, DefenseOptions? options = null, ILogger<SosThreatSink>? logger = null)
    {
        _sos = sos ?? throw new ArgumentNullException(nameof(sos));
        _options = options ?? new DefenseOptions();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task HandleAsync(ThreatSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Severity < _options.SosSeverityFloor)
            return;

        _logger?.LogWarning(
            "SOS escalation for {Category} threat on '{Indicator}' (severity {Severity}).",
            signal.Category, signal.Indicator, signal.Severity);

        await _sos.EscalateAsync(signal, ct).ConfigureAwait(false);
    }
}
