// CoreMemory.cs
//
// The top tier of the hierarchy — memories the AI will never forget.
// Promoted from any lower tier when salience crosses the core threshold,
// or written directly by the host for known-permanent facts (the user's
// name, their birthday, the names of their family).

using System;
using System.Collections.Generic;

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Why a memory was promoted to the core tier. Determines how the host UI
/// might render it.
/// </summary>
public enum CoreMemoryKind
{
    /// <summary>
    /// A factual statement the user explicitly asked the AI to remember
    /// (e.g. "Remember that my daughter's name is Alex").
    /// </summary>
    UserAsserted,

    /// <summary>
    /// Inferred from interaction patterns — a long-standing preference,
    /// recurring topic, important relationship reference.
    /// </summary>
    PatternInferred,

    /// <summary>
    /// Promoted because of extreme salience (very high satisfaction,
    /// emotionally significant moment, life-event mention).
    /// </summary>
    HighSalience,

    /// <summary>
    /// Promoted by the host directly (e.g. profile sync, identity bootstrap).
    /// </summary>
    HostProvided,
}

/// <summary>
/// A core memory the AI will not forget. Compact by design — the core tier
/// is small even after years of consolidation.
/// </summary>
public sealed class CoreMemory
{
    /// <summary>Stable identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC time the memory was committed to core.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC time the memory was last reinforced (re-asserted, re-cited).</summary>
    public DateTimeOffset LastReinforcedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Short, dense statement of the memory — written in third person from
    /// the AI's perspective (e.g. "Tony's daughter is named Alex").
    /// </summary>
    public string Statement { get; init; } = string.Empty;

    /// <summary>How the memory came to be in core.</summary>
    public CoreMemoryKind Kind { get; init; }

    /// <summary>
    /// Optional topic label (e.g. "family", "career", "health").
    /// </summary>
    public string? Topic { get; init; }

    /// <summary>
    /// Embedding of <see cref="Statement"/> for retrieval. Null when no
    /// embedding backend was available at commit time.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// How many times this memory has been reinforced — re-asserted by the
    /// user, cited by the AI, or matched in retrieval. Used to break ties
    /// when core grows large and the host wants to prioritise display.
    /// </summary>
    public int ReinforcementCount { get; set; }

    /// <summary>
    /// Trace back to the lower-tier source memory if one exists (e.g. the
    /// daily summary or semantic cluster from which this was promoted).
    /// </summary>
    public Guid? SourceMemoryId { get; init; }
}
