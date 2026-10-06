// AuthorizedUseRequest.cs
//
// What the run path presents to the authorized-use gate before any antibody
// executes. It pairs the capability being requested with the defined threat that
// justifies it. The gate inspects this and returns an AuthorizationDecision.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// A request presented to an <see cref="IAuthorizedUseGate"/> asking permission to
/// run a single <see cref="AntibodyCapability"/> under a specific
/// <see cref="DefensiveThreatContext"/>. Construction is the only way a capability
/// reaches the gate; the gate is the only thing that can grant it.
/// </summary>
/// <param name="RequestId">Unique id for this request; echoed on the decision for correlation.</param>
/// <param name="Capability">The capability the caller wants to run.</param>
/// <param name="Threat">The defined threat that justifies running it.</param>
/// <param name="Justification">Short, specific reason this capability is needed for this threat.</param>
/// <param name="RequestedAtUtc">When the request was made.</param>
public sealed record AuthorizedUseRequest(
    Guid RequestId,
    AntibodyCapability Capability,
    DefensiveThreatContext Threat,
    string Justification,
    DateTimeOffset RequestedAtUtc)
{
    /// <summary>
    /// Creates a request for <paramref name="capability"/> under
    /// <paramref name="threat"/>, stamped with the current time and a fresh id.
    /// Throws if the threat is null or the justification is blank — the gate must
    /// never be asked without a defined threat and a stated reason.
    /// </summary>
    public static AuthorizedUseRequest For(
        AntibodyCapability capability,
        DefensiveThreatContext threat,
        string justification,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(threat);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        var clock = timeProvider ?? TimeProvider.System;
        return new AuthorizedUseRequest(
            Guid.NewGuid(),
            capability,
            threat,
            justification,
            clock.GetUtcNow());
    }
}
