using Circle.AI.Memory;
using Circle.AI.Security;

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

    // ─────────────────────────────────────────────────────────────────────────
    //  Anomaly bridge — Security → Orchestration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a confirmed <see cref="AnomalySignal"/> from the local immune system
    /// into an <see cref="AgentTask"/> for an ops-security agent. Returns
    /// <c>null</c> for signals below the dispatch threshold.
    /// </summary>
    /// <param name="signal">The anomaly signal to evaluate.</param>
    /// <param name="dispatchThreshold">
    /// Minimum <see cref="AnomalySignal.Confidence"/> required to dispatch.
    /// Default 0.30 — matches <c>DefaultSecurityWatchdog</c>'s rotation threshold.
    /// </param>
    /// <returns>
    /// An <see cref="AgentTask"/> tagged with <see cref="AgentRole.Security"/>
    /// and priority derived from the signal's confidence; or <c>null</c> if the
    /// signal is below threshold.
    /// </returns>
    public static AgentTask? FromAnomalySignal(
        AnomalySignal signal,
        double dispatchThreshold = 0.30)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Confidence < dispatchThreshold) return null;

        // Confidence drives priority — high-severity vectors are bumped one rank.
        var priority = signal.Confidence switch
        {
            >= 0.85 => AgentPriority.Critical,
            >= 0.60 => AgentPriority.High,
            _       => AgentPriority.Normal,
        };

        bool isHighSeverityVector = signal.Vector is
            ThreatVector.ControlFlowDrift or
            ThreatVector.PrivilegeEscalation or
            ThreatVector.NetworkPivot or
            ThreatVector.StateCorruption;

        if (isHighSeverityVector && priority > AgentPriority.Critical /* lower numeric is higher */)
        {
            // priority ordering: Critical=0 < High=1 < Normal=2 < Low=3
            // "bumping one rank" means decreasing the numeric value
            priority = (AgentPriority)Math.Max((int)AgentPriority.Critical, (int)priority - 1);
        }

        var inputs = new Dictionary<string, string>(signal.Evidence)
        {
            ["signal_id"]         = signal.Id.ToString(),
            ["vector"]            = signal.Vector.ToString(),
            ["confidence"]        = signal.Confidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            ["affected_module"]   = signal.AffectedModule,
            ["description"]       = signal.Description,
            ["detected_at"]       = signal.DetectedAt.ToString("O"),
        };

        return AgentTask.Create(
            AgentRole.Security,
            $"ops-security: anomaly {signal.Vector} in {signal.AffectedModule} " +
            $"(confidence {signal.Confidence:P0})",
            priority,
            inputs);
    }
}
