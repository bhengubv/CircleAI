// ThreatAwarenessVerdict.cs
//
// The verdict an assessment reaches. Deliberately conservative: a clean lookup is
// "no KNOWN threat", never "safe". Absence of evidence is not evidence of safety,
// and the guidance the user receives says so.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// The outcome of a defensive threat-awareness assessment.
/// </summary>
public enum ThreatAwarenessVerdict
{
    /// <summary>
    /// No assessment was performed — e.g. the authorized-use gate denied it.
    /// The default value, so an unset result reads as "nothing was checked".
    /// </summary>
    NotAssessed,

    /// <summary>
    /// The indicator did not match anything known-bad in the local corpus. This is
    /// NOT a clean bill of health — it means "no known threat", nothing stronger.
    /// </summary>
    NoKnownThreat,

    /// <summary>The indicator matched something the local corpus flags as suspicious.</summary>
    Suspicious,

    /// <summary>The indicator matched something the local corpus flags as known-bad.</summary>
    KnownBad,

    /// <summary>
    /// The assessment could not reach a verdict (e.g. a malformed indicator). Treated
    /// with caution, not as safety.
    /// </summary>
    Inconclusive,
}
