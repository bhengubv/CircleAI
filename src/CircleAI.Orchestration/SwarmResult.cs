namespace CircleAI.Orchestration;

/// <summary>
/// The outcome produced by an agent handler for a single <see cref="AgentTask"/>.
/// </summary>
/// <param name="TaskId">The <see cref="AgentTask.Id"/> this result belongs to.</param>
/// <param name="Role">The <see cref="AgentRole"/> that produced this result.</param>
/// <param name="Status">Final lifecycle status of the task.</param>
/// <param name="Output">
/// Human-readable output produced by the agent (e.g. a diff, a report, or an
/// error message).
/// </param>
/// <param name="Issues">
/// Zero or more issue strings emitted by the agent. Prefix with
/// <c>[CRITICAL]</c> or <c>[HIGH]</c> to trigger quality-gate blocking;
/// any other prefix is treated as a warning.
/// </param>
/// <param name="CompletedAt">UTC timestamp at which the handler returned.</param>
public sealed record SwarmResult(
    Guid TaskId,
    AgentRole Role,
    AgentStatus Status,
    string Output,
    IReadOnlyList<string> Issues,
    DateTimeOffset CompletedAt);
