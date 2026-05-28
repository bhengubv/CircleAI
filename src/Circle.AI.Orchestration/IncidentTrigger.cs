using Circle.AI.Memory;

namespace Circle.AI.Orchestration;

/// <summary>
/// Maps a recorded <see cref="EpisodicMemoryEntry"/> to the set of agent tasks
/// that should be triggered when the entry represents a crash or security incident.
/// </summary>
public static class IncidentTrigger
{
    /// <summary>
    /// Tag keys on an <see cref="EpisodicMemoryEntry"/> that identify it as a
    /// crash or unhandled-error incident.
    /// </summary>
    private static readonly HashSet<string> CrashTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "crash",
        "exception",
        "unhandled_error",
        "oom",
        "null_reference",
    };

    /// <summary>
    /// Tag keys that, in addition to a crash signal, indicate a security
    /// investigation is warranted.
    /// </summary>
    private static readonly HashSet<string> SecurityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth_failure",
        "permission_denied",
        "token_expired",
        "injection",
        "overflow",
    };

    /// <summary>
    /// Inspects an episodic memory entry and returns the agent tasks that should
    /// be triggered. Returns an empty list when the entry is not an incident.
    /// </summary>
    /// <param name="entry">
    /// The episodic memory entry to evaluate. Must not be <c>null</c>.
    /// </param>
    /// <returns>
    /// An <see cref="IReadOnlyList{T}"/> of <see cref="AgentTask"/> items:
    /// <list type="bullet">
    ///   <item><description>
    ///     One <see cref="AgentRole.Operations"/> task is always included when a
    ///     crash tag is detected.
    ///   </description></item>
    ///   <item><description>
    ///     One <see cref="AgentRole.Security"/> task is additionally included
    ///     when a security tag is also present.
    ///   </description></item>
    /// </list>
    /// </returns>
    public static IReadOnlyList<AgentTask> FromMemoryEntry(EpisodicMemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var tags = entry.Tags ?? new Dictionary<string, string>();
        bool isCrash = tags.Keys.Any(k => CrashTags.Contains(k));
        if (!isCrash)
            return Array.Empty<AgentTask>();

        var tasks = new List<AgentTask>();

        // Always dispatch an ops-incident task for every crash entry.
        tasks.Add(AgentTask.Create(
            AgentRole.Operations,
            $"ops-incident: diagnose crash recorded at {entry.RecordedAtUtc:O}",
            AgentPriority.High,
            new Dictionary<string, string>
            {
                ["episode_id"]       = entry.Id.ToString(),
                ["user_text"]        = entry.UserText,
                ["assistant_text"]   = entry.AssistantText,
                ["app_context"]      = entry.AppContext ?? string.Empty,
            }));

        // When security indicators are also present, escalate to a security agent.
        bool isSecurity = tags.Keys.Any(k => SecurityTags.Contains(k));
        if (isSecurity)
        {
            tasks.Add(AgentTask.Create(
                AgentRole.Security,
                $"ops-security: investigate security incident from episode {entry.Id}",
                AgentPriority.Critical,
                new Dictionary<string, string>
                {
                    ["episode_id"]  = entry.Id.ToString(),
                    ["app_context"] = entry.AppContext ?? string.Empty,
                    ["tags"]        = string.Join(",", tags.Keys),
                }));
        }

        return tasks;
    }
}
