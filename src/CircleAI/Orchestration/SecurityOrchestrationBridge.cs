// SecurityOrchestrationBridge.cs
//
// Bridges CircleAI.Security's ISecurityWatchdog to a LokiOrchestrator.
//
// When the local immune system detects a confirmed anomaly, this wrapper:
//   1. Delegates the runtime response to the inner watchdog (key rotation,
//      mesh isolation, state rollback) — fast path, in-process.
//   2. IN PARALLEL, dispatches an ops-security AgentTask to the orchestrator
//      so a background agent swarm can perform deeper diagnostics, generate
//      a patch, and pass it through BugHunter quality gates.
//
// The two paths are independent — the immediate watchdog response is never
// blocked by agent orchestration, and agent failures never break the runtime
// response.

using CircleAI.Security;

namespace CircleAI.Orchestration;

/// <summary>
/// Wraps an <see cref="ISecurityWatchdog"/> so that every anomaly signal
/// also dispatches an ops-security <see cref="AgentTask"/> to a
/// <see cref="LokiOrchestrator"/>. Runtime response and agent dispatch
/// proceed in parallel; neither blocks the other.
/// </summary>
public sealed class SecurityOrchestrationBridge : ISecurityWatchdog
{
    private readonly ISecurityWatchdog _inner;
    private readonly LokiOrchestrator _orchestrator;
    private readonly double _dispatchThreshold;

    /// <summary>
    /// Creates a bridge that delegates immune-system responses to
    /// <paramref name="inner"/> and dispatches ops-security agents via
    /// <paramref name="orchestrator"/>.
    /// </summary>
    /// <param name="inner">Underlying watchdog (typically <see cref="DefaultSecurityWatchdog"/>).</param>
    /// <param name="orchestrator">Orchestrator that runs the dispatched agent swarms.</param>
    /// <param name="dispatchThreshold">
    /// Minimum <see cref="AnomalySignal.Confidence"/> required to dispatch an agent.
    /// Default 0.30 — matches the inner watchdog's rotation threshold.
    /// </param>
    public SecurityOrchestrationBridge(
        ISecurityWatchdog inner,
        LokiOrchestrator orchestrator,
        double dispatchThreshold = 0.30)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(orchestrator);
        _inner = inner;
        _orchestrator = orchestrator;
        _dispatchThreshold = dispatchThreshold;
    }

    /// <inheritdoc/>
    public async Task<SecurityResponse> OnAnomalyDetectedAsync(
        AnomalySignal signal,
        SecurityCheckpoint? checkpoint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // Run the immediate immune-system response and the agent dispatch
        // in parallel. The runtime response (key rotation, rollback) MUST NOT
        // wait on the agent swarm, which may take minutes.
        var watchdogTask = _inner.OnAnomalyDetectedAsync(signal, checkpoint, ct);
        var agentTask    = DispatchAgentAsync(signal, ct);

        // Await the watchdog so the caller gets the runtime response immediately.
        var response = await watchdogTask.ConfigureAwait(false);

        // Fire-and-forget on the agent path — observe completion only to
        // surface unexpected exceptions to the runtime.
        _ = agentTask.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    // Swallowing here is intentional: agent failures must not
                    // crash the runtime. Host applications can subscribe to
                    // LokiOrchestrator results for visibility.
                    _ = t.Exception.Flatten();
                }
            },
            TaskScheduler.Default);

        return response;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<AnomalySignal> StreamSignalsAsync(CancellationToken ct = default) =>
        _inner.StreamSignalsAsync(ct);

    private async Task DispatchAgentAsync(AnomalySignal signal, CancellationToken ct)
    {
        var task = IncidentTrigger.FromAnomalySignal(signal, _dispatchThreshold);
        if (task is null) return;

        // Drain the swarm enumerator — typically a single task → single result.
        await foreach (var _ in _orchestrator.RunSwarmAsync(new[] { task }, ct).ConfigureAwait(false))
        {
            // Results are observable through orchestrator subscriptions on the host side.
        }
    }
}
