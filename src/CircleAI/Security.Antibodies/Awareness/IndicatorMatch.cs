// IndicatorMatch.cs
//
// What a local corpus returns when an indicator is found. Carries the verdict plus
// human-readable, DEFENSIVE guidance the user can act on. A corpus returns null for
// "not found" — never a fabricated verdict.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// A hit from an <see cref="ILocalIndicatorCorpus"/>: the verdict for a matched
/// indicator and the defensive context to relay to the user.
/// </summary>
/// <param name="Kind">The kind of indicator that matched.</param>
/// <param name="Verdict">The corpus's verdict for the match.</param>
/// <param name="Note">Short description of why the indicator is flagged.</param>
/// <param name="ProtectiveGuidance">What the user should do to stay safe.</param>
/// <param name="Source">Which local corpus / dataset the match came from, for auditability.</param>
public sealed record IndicatorMatch(
    IndicatorKind Kind,
    ThreatAwarenessVerdict Verdict,
    string Note,
    string ProtectiveGuidance,
    string Source);
