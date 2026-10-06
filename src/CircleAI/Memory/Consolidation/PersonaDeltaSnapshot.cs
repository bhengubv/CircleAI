// PersonaDeltaSnapshot.cs
//
// Captures how the user's PersonaState changed across one consolidation
// period (typically a month). This is the longitudinal record of how the
// AI's model of the user evolves — the data behind "she's grown to know
// what I actually like".

using System;
using System.Collections.Generic;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Diff between a <see cref="PersonaState"/> at the start and end of a
/// consolidation period. Retained forever — the longitudinal record of
/// the AI's evolving understanding of the user.
/// </summary>
public sealed class PersonaDeltaSnapshot
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC time the delta was captured.</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Start of the period (date only, UTC).</summary>
    public DateOnly PeriodStart { get; init; }

    /// <summary>End of the period (date only, UTC).</summary>
    public DateOnly PeriodEnd { get; init; }

    /// <summary>
    /// User identifier (matches <see cref="PersonaState.UserId"/>).
    /// </summary>
    public string UserId { get; init; } = "default";

    /// <summary>
    /// Verbosity at period start.
    /// </summary>
    public string VerbosityBefore { get; init; } = string.Empty;

    /// <summary>Verbosity at period end.</summary>
    public string VerbosityAfter { get; init; } = string.Empty;

    /// <summary>Formality at period start.</summary>
    public string FormalityBefore { get; init; } = string.Empty;

    /// <summary>Formality at period end.</summary>
    public string FormalityAfter { get; init; } = string.Empty;

    /// <summary>
    /// New topics that emerged in the period (key) along with the weight
    /// they accumulated (value). Excludes topics already known at period
    /// start.
    /// </summary>
    public IReadOnlyDictionary<string, float> NewTopics { get; init; }
        = new Dictionary<string, float>();

    /// <summary>
    /// Topics that gained the most weight during the period. Key = topic,
    /// value = weight delta.
    /// </summary>
    public IReadOnlyDictionary<string, float> StrengthenedTopics { get; init; }
        = new Dictionary<string, float>();

    /// <summary>
    /// Topics that the user explicitly down-voted during the period.
    /// Appended to <see cref="PersonaState.DisfavouredTopics"/>.
    /// </summary>
    public IReadOnlyList<string> NewlyDisfavouredTopics { get; init; }
        = Array.Empty<string>();

    /// <summary>
    /// Net positive minus negative signals across the period.
    /// </summary>
    public int NetSignalDelta { get; init; }

    /// <summary>
    /// Total interactions during the period.
    /// </summary>
    public int InteractionsInPeriod { get; init; }

    /// <summary>
    /// Short human-readable narrative of how the persona changed —
    /// suitable for surfacing in a "how she's grown" UI panel.
    /// </summary>
    public string Narrative { get; init; } = string.Empty;
}
