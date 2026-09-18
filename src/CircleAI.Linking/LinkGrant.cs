// LinkGrant.cs
//
// A standing authorization for one app to use CircleAI on this device.
//
// THE SIGNATURE IS PART OF THE IDENTITY, NOT DECORATION. A package name is not a
// secret — any app can claim any package name to a service it binds. What cannot
// be forged is the signing certificate the OS reports for the calling app. So a
// grant is keyed on the package but ALSO records the signature it was minted
// against, and a later call whose signature does not match is refused rather than
// served — a repackaged impostor of "com.bank.app" gets nothing.

namespace CircleAI.Linking;

/// <summary>One app's standing permission to use CircleAI on this device.</summary>
/// <param name="CallerPackage">The linked app's package name.</param>
/// <param name="CallerSignature">
/// The signing certificate digest the OS reported when the grant was minted.
/// A call from the same package with a different signature is an impostor.
/// </param>
/// <param name="Scope">What the app may reach.</param>
/// <param name="IssuedAt">When the person approved the link.</param>
/// <param name="ExpiresAt">When it lapses, or null for a grant that does not expire.</param>
public sealed record LinkGrant(
    string CallerPackage,
    string CallerSignature,
    LinkScope Scope,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary>Is this grant still within its lifetime at <paramref name="now"/>?</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;

    /// <summary>Does this grant already cover everything <paramref name="requested"/> asks for?</summary>
    public bool Covers(LinkScope requested) => (Scope & requested) == requested;
}
