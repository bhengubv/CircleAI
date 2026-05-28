namespace Circle.AI.Orchestration;

/// <summary>
/// Represents a single unit of work dispatched to an agent swarm.
/// </summary>
/// <param name="Id">Stable unique identifier for this task.</param>
/// <param name="Role">The agent domain responsible for handling the task.</param>
/// <param name="Description">Human-readable description of the work to be performed.</param>
/// <param name="Priority">Execution urgency; lower numeric value = higher urgency.</param>
/// <param name="Inputs">
/// Arbitrary key-value inputs provided to the agent handler
/// (e.g. <c>"crash_log"</c>, <c>"affected_file"</c>).
/// </param>
/// <param name="CreatedAt">UTC timestamp at which the task was created.</param>
public sealed record AgentTask(
    Guid Id,
    AgentRole Role,
    string Description,
    AgentPriority Priority,
    IReadOnlyDictionary<string, string> Inputs,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Factory method that stamps a new <see cref="AgentTask"/> with a fresh
    /// <see cref="Guid"/> and <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    /// <param name="role">The agent domain responsible for handling the task.</param>
    /// <param name="description">Human-readable description of the work to perform.</param>
    /// <param name="priority">Execution urgency.</param>
    /// <param name="inputs">
    /// Optional key-value context passed to the handler. Pass <c>null</c> for an
    /// empty input set.
    /// </param>
    /// <returns>A freshly minted <see cref="AgentTask"/>.</returns>
    public static AgentTask Create(
        AgentRole role,
        string description,
        AgentPriority priority,
        IReadOnlyDictionary<string, string>? inputs = null)
        => new(
            Guid.NewGuid(),
            role,
            description,
            priority,
            inputs ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);
}
