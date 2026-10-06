// AuthorizationDecision.cs
//
// The gate's answer. Denied unless something explicitly says otherwise.
// The run path checks Granted and refuses to touch any capability when it is false.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// The decision an <see cref="IAuthorizedUseGate"/> returns for an
/// <see cref="AuthorizedUseRequest"/>. The default posture of the whole subsystem
/// is denial: a decision is only <see cref="Granted"/> when a gate has an explicit,
/// unexpired, capability-scoped reason to allow it.
/// </summary>
/// <param name="RequestId">The <see cref="AuthorizedUseRequest.RequestId"/> this decision answers.</param>
/// <param name="Capability">The capability the decision covers.</param>
/// <param name="Granted"><c>true</c> only if the antibody is explicitly authorized to run.</param>
/// <param name="Reason">Human-readable reason for the decision — always populated, especially on denial.</param>
/// <param name="DecidedAtUtc">When the decision was made.</param>
/// <param name="ExpiresAtUtc">
/// For a grant, when the authorization stops being valid; <c>null</c> for denials
/// or for grants with no explicit expiry.
/// </param>
public sealed record AuthorizationDecision(
    Guid RequestId,
    AntibodyCapability Capability,
    bool Granted,
    string Reason,
    DateTimeOffset DecidedAtUtc,
    DateTimeOffset? ExpiresAtUtc)
{
    /// <summary>
    /// Creates a denial for <paramref name="request"/>. This is the safe default —
    /// when in doubt, deny.
    /// </summary>
    public static AuthorizationDecision Deny(AuthorizedUseRequest request, string reason, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var clock = timeProvider ?? TimeProvider.System;
        return new AuthorizationDecision(
            request.RequestId, request.Capability, Granted: false, reason, clock.GetUtcNow(), ExpiresAtUtc: null);
    }

    /// <summary>
    /// Creates a grant for a request. Only a gate with an explicit, unexpired
    /// reason should call this.
    /// </summary>
    /// <param name="request">The request being granted.</param>
    /// <param name="reason">Why the request was granted (e.g. the authorizing consent id).</param>
    /// <param name="expiresAtUtc">When the grant expires, if applicable.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public static AuthorizationDecision Grant(
        AuthorizedUseRequest request,
        string reason,
        DateTimeOffset? expiresAtUtc = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var clock = timeProvider ?? TimeProvider.System;
        return new AuthorizationDecision(
            request.RequestId, request.Capability, Granted: true, reason, clock.GetUtcNow(), expiresAtUtc);
    }
}
