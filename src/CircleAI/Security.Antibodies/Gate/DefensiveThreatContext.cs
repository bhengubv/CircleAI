// DefensiveThreatContext.cs
//
// The "defined threat" that must accompany every antibody invocation.
// An antibody never runs "just in case" — it runs because a real, named threat
// justified it. This type is that justification, recorded and passed with the
// request. No context → no run (enforced by the run path and the gate).

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// How serious the raised threat is. Purely descriptive — it does not by itself
/// grant anything (the gate does that); it travels with the request so the gate
/// and any audit trail can reason about proportionality.
/// </summary>
public enum ThreatSeverity
{
    /// <summary>Low-signal; awareness requested out of caution.</summary>
    Informational,

    /// <summary>A concrete indicator warranted a closer look.</summary>
    Elevated,

    /// <summary>Strong signal of an active threat to the user.</summary>
    High,

    /// <summary>Imminent or in-progress threat to the user.</summary>
    Critical,
}

/// <summary>
/// An explicit, recorded statement of the threat that justifies running an
/// antibody. Required on every invocation: "produced only under a defined threat"
/// from <c>docs/SECURITY_AUTHORIZED_USE.md</c> means an antibody is inert until a
/// context like this names the threat.
/// </summary>
/// <param name="Reason">Human-readable description of the threat that justifies the assessment. Must be non-empty.</param>
/// <param name="Severity">How serious the threat is judged to be.</param>
/// <param name="RaisedBy">
/// Which defensive subsystem or user action raised this threat (e.g. a defensive
/// reflex, or an explicit user request to check a file they just received).
/// </param>
/// <param name="RaisedAtUtc">When the threat was raised.</param>
/// <param name="CorrelationId">Correlates this context with any downstream results and audit records.</param>
public sealed record DefensiveThreatContext(
    string Reason,
    ThreatSeverity Severity,
    string RaisedBy,
    DateTimeOffset RaisedAtUtc,
    Guid CorrelationId)
{
    /// <summary>
    /// Creates a threat context stamped with the current time and a fresh
    /// correlation id. Throws if <paramref name="reason"/> or
    /// <paramref name="raisedBy"/> is null or blank — an unnamed threat is not a
    /// defined threat.
    /// </summary>
    /// <param name="reason">Why the assessment is justified. Required.</param>
    /// <param name="severity">Judged severity.</param>
    /// <param name="raisedBy">Who/what raised the threat. Required.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public static DefensiveThreatContext Raise(
        string reason,
        ThreatSeverity severity,
        string raisedBy,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(raisedBy);

        var clock = timeProvider ?? TimeProvider.System;
        return new DefensiveThreatContext(
            reason,
            severity,
            raisedBy,
            clock.GetUtcNow(),
            Guid.NewGuid());
    }
}
