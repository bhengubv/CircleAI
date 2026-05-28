namespace Circle.AI.Orchestration;

/// <summary>
/// Routes agent tasks to their handlers and evaluates quality gates on results.
/// </summary>
public interface IAgentDispatcher
{
    /// <summary>
    /// Dispatches <paramref name="task"/> to the appropriate agent handler and
    /// returns the result once the handler completes (or times out).
    /// </summary>
    /// <param name="task">The task to dispatch. Must not be <c>null</c>.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>
    /// A <see cref="SwarmResult"/> reflecting the final status and output of
    /// the dispatched task.
    /// </returns>
    Task<SwarmResult> DispatchAsync(AgentTask task, CancellationToken ct = default);

    /// <summary>
    /// Evaluates the quality of a completed <see cref="SwarmResult"/> and
    /// determines whether it passes the deployment gate.
    /// </summary>
    /// <param name="result">The swarm result to evaluate. Must not be <c>null</c>.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>
    /// A <see cref="QualityGateResult"/> indicating whether the result passed
    /// and listing any blockers or warnings.
    /// </returns>
    Task<QualityGateResult> RunQualityGateAsync(SwarmResult result, CancellationToken ct = default);
}
