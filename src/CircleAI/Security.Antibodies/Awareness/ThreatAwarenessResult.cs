// ThreatAwarenessResult.cs
//
// The ONLY thing an antibody produces: awareness. A verdict plus guidance the user
// can act on — and nothing else. There is no action-taking on this surface: an
// antibody warns the user, it never quarantines, reports, or touches a third party.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// The outcome of a defensive threat-awareness assessment: what was checked, the
/// verdict, and the protective guidance for the user. This is a pure advisory —
/// it describes a warning, it does not take an action.
/// </summary>
/// <param name="IndicatorKind">What kind of thing was assessed.</param>
/// <param name="Verdict">The verdict reached.</param>
/// <param name="WasAuthorized">
/// <c>false</c> when the authorized-use gate denied the assessment, in which case
/// nothing was actually checked and <see cref="Verdict"/> is
/// <see cref="ThreatAwarenessVerdict.NotAssessed"/>.
/// </param>
/// <param name="Summary">Plain-language summary of the finding, framed for the user.</param>
/// <param name="ProtectiveGuidance">Concrete, defensive next step for the user (e.g. "do not open this file").</param>
/// <param name="Source">Where the verdict came from (the local corpus / dataset, or the gate on denial).</param>
/// <param name="AssessedAtUtc">When the assessment ran.</param>
public sealed record ThreatAwarenessResult(
    IndicatorKind IndicatorKind,
    ThreatAwarenessVerdict Verdict,
    bool WasAuthorized,
    string Summary,
    string ProtectiveGuidance,
    string Source,
    DateTimeOffset AssessedAtUtc)
{
    /// <summary>
    /// The result returned when the authorized-use gate denied the assessment.
    /// Nothing was checked; the reason is carried through from the gate.
    /// </summary>
    public static ThreatAwarenessResult NotAuthorized(
        IndicatorKind kind, string gateReason, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateReason);
        return new ThreatAwarenessResult(
            kind,
            ThreatAwarenessVerdict.NotAssessed,
            WasAuthorized: false,
            Summary: $"No check was performed — the authorized-use gate denied it: {gateReason}",
            ProtectiveGuidance: "Nothing was assessed. If you believe there is a real threat, raise it " +
                                "through the defensive flow so the check can be explicitly authorized.",
            Source: "authorized-use gate",
            AssessedAtUtc: (timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    /// <summary>Result for an indicator that matched nothing known-bad in the local corpus.</summary>
    public static ThreatAwarenessResult NoKnownThreat(
        IndicatorKind kind, string source, string protectiveGuidance, TimeProvider? timeProvider = null) =>
        new(kind, ThreatAwarenessVerdict.NoKnownThreat, WasAuthorized: true,
            Summary: "No match against your device's local threat set. This is not proof of safety — " +
                     "only that nothing known-bad was found.",
            ProtectiveGuidance: protectiveGuidance,
            Source: source,
            AssessedAtUtc: (timeProvider ?? TimeProvider.System).GetUtcNow());

    /// <summary>Result for an indicator the local corpus flags as suspicious.</summary>
    public static ThreatAwarenessResult Suspicious(
        IndicatorKind kind, string source, string summary, string protectiveGuidance, TimeProvider? timeProvider = null) =>
        new(kind, ThreatAwarenessVerdict.Suspicious, WasAuthorized: true,
            summary, protectiveGuidance, source, (timeProvider ?? TimeProvider.System).GetUtcNow());

    /// <summary>Result for an indicator the local corpus flags as known-bad.</summary>
    public static ThreatAwarenessResult KnownBad(
        IndicatorKind kind, string source, string summary, string protectiveGuidance, TimeProvider? timeProvider = null) =>
        new(kind, ThreatAwarenessVerdict.KnownBad, WasAuthorized: true,
            summary, protectiveGuidance, source, (timeProvider ?? TimeProvider.System).GetUtcNow());

    /// <summary>Result when the assessment ran but could not reach a verdict.</summary>
    public static ThreatAwarenessResult Inconclusive(
        IndicatorKind kind, string source, string protectiveGuidance, TimeProvider? timeProvider = null) =>
        new(kind, ThreatAwarenessVerdict.Inconclusive, WasAuthorized: true,
            Summary: "The assessment ran but could not reach a verdict for this indicator.",
            ProtectiveGuidance: protectiveGuidance,
            Source: source,
            AssessedAtUtc: (timeProvider ?? TimeProvider.System).GetUtcNow());
}
