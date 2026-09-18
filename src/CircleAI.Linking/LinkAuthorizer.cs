// LinkAuthorizer.cs
//
// The act, on top of the decision.
//
// LinkGate says what should happen; this runs it. It is still testable without a
// phone: the ONE Android-shaped thing — presenting a fingerprint / PIN prompt —
// arrives as a callback the caller supplies, so a test passes a function that
// returns "approved" or "declined" and the mint-and-store logic is exercised in
// full. The device head passes a callback backed by IAuthChallenge (CircleAI.Aether).

namespace CircleAI.Linking;

/// <summary>Turns a <see cref="LinkGate"/> decision into a stored grant, or nothing.</summary>
public sealed class LinkAuthorizer
{
    private readonly ILinkGrantStore _store;
    private readonly LinkGate _gate;
    private readonly TimeSpan _grantLifetime;

    /// <param name="store">Where grants are minted.</param>
    /// <param name="gate">The decision policy.</param>
    /// <param name="grantLifetime">
    /// How long a minted grant lives. <see cref="TimeSpan.Zero"/> means it does
    /// not expire. A finite lifetime forces periodic re-approval, which
    /// IAuthChallenge already models as PeriodicRevalidation.
    /// </param>
    public LinkAuthorizer(ILinkGrantStore store, LinkGate gate, TimeSpan grantLifetime = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);
        _store = store;
        _gate = gate;
        _grantLifetime = grantLifetime;
    }

    /// <summary>
    /// Decide, prompt if needed, and mint. Returns the grant the caller may use,
    /// or null when the request is denied or the person declines the prompt.
    /// </summary>
    /// <param name="req">The caller and the scope it wants.</param>
    /// <param name="runDeviceAuth">
    /// Presents device auth (biometric / PIN / pattern) and returns true if the
    /// person approved. Only invoked for a <see cref="LinkDecisionKind.NeedAuth"/>
    /// decision — never for a first-party or already-granted caller.
    /// </param>
    /// <param name="now">The current time (injected so expiry is testable).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<LinkGrant?> AuthorizeAsync(
        LinkRequest req,
        Func<CancellationToken, Task<bool>> runDeviceAuth,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(runDeviceAuth);

        var decision = await _gate.DecideAsync(req, now, ct).ConfigureAwait(false);

        switch (decision.Kind)
        {
            case LinkDecisionKind.Deny:
                return null;

            case LinkDecisionKind.Allow:
                // The gate already confirmed a live grant covers it.
                return await _store.FindAsync(req.CallerPackage, ct).ConfigureAwait(false);

            case LinkDecisionKind.AutoApprove:
                return await MintAsync(req, now, ct).ConfigureAwait(false);

            case LinkDecisionKind.NeedAuth:
                if (!await runDeviceAuth(ct).ConfigureAwait(false))
                    return null;   // person declined, or auth failed
                return await MintAsync(req, now, ct).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private async Task<LinkGrant> MintAsync(LinkRequest req, DateTimeOffset now, CancellationToken ct)
    {
        var expires = _grantLifetime <= TimeSpan.Zero ? (DateTimeOffset?)null : now + _grantLifetime;
        var grant = new LinkGrant(req.CallerPackage, req.CallerSignature, req.Requested, now, expires);
        await _store.SaveAsync(grant, ct).ConfigureAwait(false);
        return grant;
    }
}
