// ISecurityWatchdog.cs
//
// The central contract for the CircleAI local runtime immune system.
//
// Detection sites (companion pipeline, biometric verifier, agent patch gate)
// call OnAnomalyDetectedAsync when they observe something suspicious.
// The watchdog implementation decides the response:
//   key rotation, session revocation, mesh isolation, or state rollback.
//
// The SDK ships DefaultSecurityWatchdog as the out-of-box implementation.
// Host applications can substitute their own (e.g. one that also pages
// the ops-security agent via CircleAI.Orchestration).

using CircleAI.Core.Components;
using CircleAI.Core.Validation;

namespace CircleAI.Security;

/// <summary>
/// Central contract for the CircleAI local runtime immune system.
/// Receives <see cref="AnomalySignal"/> instances from detection sites
/// and returns the <see cref="SecurityResponse"/> describing protective action taken.
/// </summary>
public interface ISecurityWatchdog
{
    /// <summary>
    /// Called by any detection site when a local runtime anomaly is observed.
    /// The watchdog evaluates <paramref name="signal"/> and applies the
    /// appropriate protective response.
    /// </summary>
    /// <param name="signal">The detected anomaly.</param>
    /// <param name="checkpoint">
    /// The most recent <see cref="SecurityCheckpoint"/> for the affected module,
    /// if one is available. Passed so the watchdog can roll back state without
    /// needing to hold a reference to it itself.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SecurityResponse"/> describing what protective action was taken.
    /// </returns>
    Task<SecurityResponse> OnAnomalyDetectedAsync(
        AnomalySignal signal,
        SecurityCheckpoint? checkpoint = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a live stream of every <see cref="AnomalySignal"/> observed since
    /// the watchdog started. Completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<AnomalySignal> StreamSignalsAsync(CancellationToken ct = default);
}

/// <summary>
/// Default in-process watchdog. Applies graduated responses based on
/// <see cref="ThreatVector"/> and confidence level:
/// <list type="bullet">
///   <item>Confidence &lt; 0.3 → <see cref="SecurityResponseKind.NoAction"/></item>
///   <item>Confidence 0.3–0.6 → <see cref="SecurityResponseKind.KeyRotation"/></item>
///   <item>Confidence &gt; 0.6 + confusion/pivot/escalation → <see cref="SecurityResponseKind.Composite"/> (rotation + mesh signal)</item>
///   <item>Any checkpoint available → <see cref="SecurityResponseKind.StateRollback"/> added to composite</item>
/// </list>
/// Host applications can replace this with a watchdog that also invokes
/// <c>LokiOrchestrator</c> ops-security agents.
/// </summary>
[CircleAIVerificationStatus(VerificationLevel.WireProven,
    Notes = "In-process Channel<AnomalySignal>. Single-process correct. Not multi-replica safe — signals emitted on replica A do not reach stream subscribers on replica B.")]
public sealed class DefaultSecurityWatchdog : CircleAIComponentBase, ISecurityWatchdog
{
    private const double RotationThreshold  = 0.30;
    private const double CompositeThreshold = 0.60;

    private readonly System.Threading.Channels.Channel<AnomalySignal> _signals =
        System.Threading.Channels.Channel.CreateUnbounded<AnomalySignal>();

    /// <inheritdoc/>
    public override string ComponentName => "DefaultSecurityWatchdog";

    /// <summary>Construct the default watchdog.</summary>
    public DefaultSecurityWatchdog() : base()
    {
    }

    /// <inheritdoc/>
    public Task<SecurityResponse> OnAnomalyDetectedAsync(
        AnomalySignal signal,
        SecurityCheckpoint? checkpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return RunOperationAsync(
            "OnAnomalyDetectedAsync",
            async () =>
            {
                // Broadcast to any stream subscribers
                await _signals.Writer.WriteAsync(signal, ct).ConfigureAwait(false);

                CircleAI.Core.Diagnostics.CircleAIDiagnostics.AnomalySignalsTotal.Add(1,
                    new KeyValuePair<string, object?>("vector", signal.Vector.ToString()),
                    new KeyValuePair<string, object?>("confidence_band", signal.Confidence switch
                    {
                        < 0.30 => "low",
                        < 0.60 => "mid",
                        _ => "high"
                    }));

                // ── Graduated response policy ────────────────────────────────────────

                if (signal.Confidence < RotationThreshold)
                    return SecurityResponse.NoAction(signal.Id,
                        $"Confidence {signal.Confidence:P0} below rotation threshold — monitoring only.");

                // High-severity vectors always warrant rollback if we have a checkpoint
                bool isHighSeverity = signal.Vector is
                    ThreatVector.ControlFlowDrift or
                    ThreatVector.PrivilegeEscalation or
                    ThreatVector.NetworkPivot or
                    ThreatVector.StateCorruption;

                if (signal.Confidence > CompositeThreshold)
                {
                    var actions = new List<SecurityResponseKind>
                    {
                        SecurityResponseKind.KeyRotation,
                        SecurityResponseKind.MeshIsolationSignal,
                    };

                    SecurityCheckpoint? restored = null;
                    if (checkpoint is not null && isHighSeverity && checkpoint.Verify())
                    {
                        actions.Add(SecurityResponseKind.StateRollback);
                        restored = checkpoint;
                    }

                    return SecurityResponse.Composite(
                        signal.Id, actions,
                        $"Composite response for {signal.Vector} (confidence {signal.Confidence:P0}) " +
                        $"in {signal.AffectedModule}.",
                        restored);
                }

                // Mid-range confidence: rotate keys only
                return SecurityResponse.ForKeyRotation(signal.Id,
                    $"Key rotation triggered for {signal.Vector} (confidence {signal.Confidence:P0}) " +
                    $"in {signal.AffectedModule}.");
            },
            ct,
            correlationId: signal.Id.ToString());
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<AnomalySignal> StreamSignalsAsync(CancellationToken ct = default)
    {
        return RunStreamAsync<AnomalySignal>(
            "StreamSignalsAsync",
            (c) => _signals.Reader.ReadAllAsync(c),
            ct);
    }
}
