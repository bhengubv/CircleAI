// SleepKind.cs
//
// Identifies which tier of consolidation the engine should run during a tick.
// Named "sleep" because in the Her/Jarvis analogy this is the phase where the
// AI re-organises its day's experiences into longer-lived memory — exactly the
// role human sleep plays.

namespace CircleAI.Memory.Consolidation;

/// <summary>
/// Which tier of hierarchical consolidation a <see cref="IMemoryConsolidator"/>
/// tick should run. Each kind compresses one tier into the next.
/// </summary>
public enum SleepKind
{
    /// <summary>
    /// End-of-day pass: collapse the day's <see cref="EpisodicMemoryEntry"/>
    /// records into a single <see cref="DailyMemorySummary"/>.
    /// </summary>
    Daily,

    /// <summary>
    /// End-of-week pass: cluster the week's daily summaries into semantic
    /// topic groups (<see cref="SemanticMemoryCluster"/>).
    /// </summary>
    Weekly,

    /// <summary>
    /// End-of-month pass: compute the <see cref="PersonaState"/> delta over the
    /// month and write a <see cref="PersonaDeltaSnapshot"/>.
    /// </summary>
    Monthly,

    /// <summary>
    /// Caller-initiated pass — typically used for tests, manual maintenance,
    /// or "consolidate everything that's currently due" workflows. The engine
    /// will run whichever tiers have not yet been consolidated for the current
    /// period.
    /// </summary>
    OnDemand,
}
