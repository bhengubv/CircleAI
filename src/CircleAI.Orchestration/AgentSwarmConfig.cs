namespace CircleAI.Orchestration;

/// <summary>
/// Tuning parameters that govern how <see cref="LokiOrchestrator"/> schedules
/// and enforces quality gates across a swarm of agent tasks.
/// </summary>
/// <param name="MaxConcurrency">
/// Maximum number of tasks that may execute simultaneously.
/// Defaults to <c>4</c>.
/// </param>
/// <param name="TaskTimeout">
/// Maximum wall-clock time allowed for a single task before it is cancelled
/// and marked <see cref="AgentStatus.Failed"/>.
/// Defaults to 5 minutes.
/// </param>
/// <param name="RequireReviewPassBeforeDeploy">
/// When <c>true</c>, any <see cref="AgentRole.Review"/> result that fails the
/// quality gate will prevent downstream deployment steps.
/// Defaults to <c>true</c>.
/// </param>
/// <param name="RequireSecurityPassBeforeDeploy">
/// When <c>true</c>, any <see cref="AgentRole.Security"/> result that fails the
/// quality gate will prevent downstream deployment steps.
/// Defaults to <c>true</c>.
/// </param>
public sealed record AgentSwarmConfig(
    int MaxConcurrency,
    TimeSpan TaskTimeout,
    bool RequireReviewPassBeforeDeploy,
    bool RequireSecurityPassBeforeDeploy)
{
    /// <summary>
    /// Production-safe defaults: 4 concurrent tasks, 5-minute timeout, both
    /// review and security gates enforced.
    /// </summary>
    public static AgentSwarmConfig Default => new(4, TimeSpan.FromMinutes(5), true, true);
}
