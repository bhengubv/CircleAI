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
// the ops-security agent via Circle.AI.Orchestration).

namespace Circle.AI.Security;

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
public sealed class DefaultSecurityWatchdog : ISecurityWatchdog
{
    private const double RotationThreshold  = 0.30;
    private const double CompositeThreshold = 0.60;

    private readonly System.Threading.Channels.Channel<AnomalySignal> _signals =
        System.Threading.Channels.Channel.CreateUnbounded<AnomalySignal>();

    /// <inheritdoc/>
    public async Task<SecurityResponse> OnAnomalyDetectedAsync(
        AnomalySignal signal,
        SecurityCheckpoint? checkpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // Broadcast to any stream subscribers
        await _signals.Writer.WriteAsync(signal, ct).ConfigureAwait(false);

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
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AnomalySignal> StreamSignalsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var signal in _signals.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return signal;
    }
}
