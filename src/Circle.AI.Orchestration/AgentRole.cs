namespace Circle.AI.Orchestration;

/// <summary>
/// Categorises the domain responsibility of an agent in a swarm.
/// </summary>
public enum AgentRole
{
    /// <summary>
    /// Responsible for writing, reviewing, and fixing code.
    /// </summary>
    Engineering,

    /// <summary>
    /// Responsible for infrastructure, deployments, and incident response.
    /// </summary>
    Operations,

    /// <summary>
    /// Responsible for quality review, testing, and acceptance criteria.
    /// </summary>
    Review,

    /// <summary>
    /// Responsible for security analysis and vulnerability assessment.
    /// </summary>
    Security,
}

/// <summary>
/// Execution priority of an agent task. Lower numeric value = higher urgency.
/// </summary>
public enum AgentPriority
{
    /// <summary>
    /// Immediate — blocks all other work until resolved.
    /// </summary>
    Critical = 0,

    /// <summary>
    /// Urgent — should be addressed in the current session.
    /// </summary>
    High = 1,

    /// <summary>
    /// Standard — processed in arrival order.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// Best-effort — processed only when no higher-priority work is pending.
    /// </summary>
    Low = 3,
}

/// <summary>
/// Lifecycle status of an agent task or swarm result.
/// </summary>
public enum AgentStatus
{
    /// <summary>Task has been created but not yet dispatched.</summary>
    Pending,

    /// <summary>Task is currently being executed by a handler.</summary>
    Running,

    /// <summary>Task completed and all quality gates passed.</summary>
    Passed,

    /// <summary>Task completed but produced an error or exception.</summary>
    Failed,

    /// <summary>Task was halted by a quality gate or missing handler.</summary>
    Blocked,
}
