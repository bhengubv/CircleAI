// LinkGate.cs
//
// The decision, kept separate from the act.
//
// This class decides WHETHER a caller may proceed; it never performs the device
// auth and never mints a grant. That split is deliberate: the decision is pure
// and testable without a phone, a fingerprint reader, or a store write, and the
// Android layer that CAN prompt a person is the only thing that acts on the
// decision. Four outcomes, and each maps to exactly one thing the caller does:
//
//   Allow        an existing grant already covers it            -> serve
//   AutoApprove  first-party (our own signing key), no prompt   -> mint, serve
//   NeedAuth     a person must approve with biometric/PIN        -> prompt, then mint
//   Deny         missing identity, or a signature that does not
//                match the stored grant (an impostor)            -> refuse

namespace CircleAI.Linking;

/// <summary>What the caller should do next about a link request.</summary>
public enum LinkDecisionKind
{
    /// <summary>An existing, live grant already covers the request. Serve it.</summary>
    Allow,

    /// <summary>A trusted first-party caller. Mint a grant silently and serve.</summary>
    AutoApprove,

    /// <summary>A person must approve with device auth before a grant is minted.</summary>
    NeedAuth,

    /// <summary>Refuse — no identity, or a signature mismatch against a stored grant.</summary>
    Deny,
}

/// <summary>A caller asking to use CircleAI.</summary>
/// <param name="CallerPackage">The calling app's package name (from the OS, not the app).</param>
/// <param name="CallerSignature">The calling app's signing digest (from the OS).</param>
/// <param name="Requested">The scope it wants; default a link is <see cref="LinkScope.Chat"/>.</param>
public sealed record LinkRequest(
    string CallerPackage,
    string CallerSignature,
    LinkScope Requested = LinkScope.Chat);

/// <summary>The gate's verdict on a <see cref="LinkRequest"/>.</summary>
public sealed record LinkDecision(LinkDecisionKind Kind, LinkScope Scope, string Reason)
{
    public static LinkDecision Allow(LinkScope scope) =>
        new(LinkDecisionKind.Allow, scope, "an existing grant covers the request");

    public static LinkDecision AutoApprove(LinkScope scope) =>
        new(LinkDecisionKind.AutoApprove, scope, "first-party signing key");

    public static LinkDecision NeedAuth(LinkScope scope, string why) =>
        new(LinkDecisionKind.NeedAuth, scope, why);

    public static LinkDecision Deny(string why) =>
        new(LinkDecisionKind.Deny, LinkScope.None, why);
}

/// <summary>Decides whether a caller may use CircleAI — without acting on it.</summary>
public sealed class LinkGate
{
    private readonly ILinkGrantStore _store;
    private readonly IReadOnlySet<string> _firstPartySignatures;

    /// <param name="store">Where standing grants live.</param>
    /// <param name="firstPartySignatures">
    /// Signing digests that are trusted without a prompt — our own apps, signed
    /// with the same key. Empty means every caller is treated as third-party.
    /// </param>
    public LinkGate(ILinkGrantStore store, IReadOnlySet<string>? firstPartySignatures = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _firstPartySignatures = firstPartySignatures ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Decide what should happen for <paramref name="req"/> as of <paramref name="now"/>.</summary>
    public async Task<LinkDecision> DecideAsync(
        LinkRequest req, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (string.IsNullOrWhiteSpace(req.CallerPackage) ||
            string.IsNullOrWhiteSpace(req.CallerSignature))
            return LinkDecision.Deny("missing caller identity");

        if (req.Requested == LinkScope.None)
            return LinkDecision.Deny("empty scope requested");

        var existing = await _store.FindAsync(req.CallerPackage, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // The package is known; the signature must still match the one it
            // linked with. A repackaged impostor is refused, not re-prompted.
            if (!string.Equals(existing.CallerSignature, req.CallerSignature, StringComparison.Ordinal))
                return LinkDecision.Deny("caller signature does not match the stored grant");

            if (existing.IsLiveAt(now) && existing.Covers(req.Requested))
                return LinkDecision.Allow(existing.Scope);

            // Same app, but it wants more than it was granted, or the grant lapsed.
            var why = existing.IsLiveAt(now) ? "scope escalation" : "grant expired";
            return _firstPartySignatures.Contains(req.CallerSignature)
                ? LinkDecision.AutoApprove(req.Requested)
                : LinkDecision.NeedAuth(req.Requested, why);
        }

        // Never linked. Our own apps skip the prompt; everyone else asks a person.
        return _firstPartySignatures.Contains(req.CallerSignature)
            ? LinkDecision.AutoApprove(req.Requested)
            : LinkDecision.NeedAuth(req.Requested, "no grant yet");
    }
}
