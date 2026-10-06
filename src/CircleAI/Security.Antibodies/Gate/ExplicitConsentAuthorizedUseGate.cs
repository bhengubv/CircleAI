// ExplicitConsentAuthorizedUseGate.cs
//
// A gate that CAN grant — but only on an explicit, unexpired, capability-scoped
// consent, and only when a defined threat accompanies the request. Everything else
// denies. This is how a host opts a single capability in for a single window
// without ever loosening the library's deny-by-default default.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// An <see cref="IAuthorizedUseGate"/> that grants only when its
/// <see cref="IAuthorizedUseConsentStore"/> holds an active
/// <see cref="AuthorizedUseConsent"/> for the requested capability. With an empty
/// store it behaves exactly like <see cref="NullAuthorizedUseGate"/> — it denies.
/// It is still deny-by-default; consent is the sole, narrow path to a grant.
/// </summary>
/// <remarks>
/// Verification: deterministic given the store and clock. The gate never grants
/// without (1) a non-null defined threat on the request and (2) a live, matching
/// consent. Any missing precondition yields a denial with a specific reason.
/// </remarks>
public sealed class ExplicitConsentAuthorizedUseGate : IAuthorizedUseGate
{
    private readonly IAuthorizedUseConsentStore _consents;
    private readonly TimeProvider _clock;

    /// <summary>Creates the gate over a consent store.</summary>
    /// <param name="consents">Where explicit consents are read from.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public ExplicitConsentAuthorizedUseGate(IAuthorizedUseConsentStore consents, TimeProvider? timeProvider = null)
    {
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async ValueTask<AuthorizationDecision> RequestAuthorizationAsync(
        AuthorizedUseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A gate must never grant without a defined threat, even if consent exists.
        if (request.Threat is null || string.IsNullOrWhiteSpace(request.Threat.Reason))
            return AuthorizationDecision.Deny(request,
                "No defined threat accompanies the request; antibodies run only under a defined threat.", _clock);

        var now = _clock.GetUtcNow();
        var consent = await _consents.FindActiveConsentAsync(request.Capability, now, ct).ConfigureAwait(false);

        if (consent is null)
            return AuthorizationDecision.Deny(request,
                $"No active authorized-use consent for {request.Capability}; denied by default.", _clock);

        return AuthorizationDecision.Grant(request,
            $"Authorized by consent {consent.ConsentId} (granted by {consent.GrantedBy}).",
            consent.ExpiresAtUtc, _clock);
    }
}
