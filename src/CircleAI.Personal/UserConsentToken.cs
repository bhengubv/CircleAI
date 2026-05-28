// UserConsentToken.cs
//
// Permission-gated consent token. Every Personal adapter requires one of these,
// signed by the user's UhidKeyRing. Signature validation lives outside this
// package — the field is preserved verbatim for the caller to verify.

namespace CircleAI.Personal;

/// <summary>
/// The set of consent scopes a <see cref="UserConsentToken"/> may grant.
/// </summary>
/// <remarks>
/// <see cref="EmailDraft"/> covers creating drafts; sending email crosses an
/// explicit trust boundary and is intentionally not exposed in this package.
/// </remarks>
public enum ConsentScope
{
    /// <summary>Read calendar events.</summary>
    CalendarRead,

    /// <summary>Create, update, or delete calendar events.</summary>
    CalendarWrite,

    /// <summary>Read inbox messages.</summary>
    EmailRead,

    /// <summary>Create draft replies. Does NOT grant send.</summary>
    EmailDraft,

    /// <summary>Read the user's contacts.</summary>
    ContactsRead,
}

/// <summary>
/// A user consent token authorising a specific set of <see cref="ConsentScope"/>s
/// against a Personal adapter.
/// </summary>
/// <param name="Id">Stable identifier for this token.</param>
/// <param name="UhidIdentityId">The Uhid identity this token is bound to.</param>
/// <param name="Scopes">Granted scopes.</param>
/// <param name="GrantedAt">UTC time the user granted consent.</param>
/// <param name="ExpiresAt">UTC time after which this token is no longer valid.</param>
/// <param name="Signature">Detached signature produced by the user's <c>UhidKeyRing</c>. Validation is performed externally.</param>
public sealed record UserConsentToken(
    Guid Id,
    string UhidIdentityId,
    IReadOnlyList<ConsentScope> Scopes,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    byte[] Signature
)
{
    /// <summary>
    /// Returns true when <paramref name="scope"/> is granted and <paramref name="now"/>
    /// is before <see cref="ExpiresAt"/>.
    /// </summary>
    /// <param name="scope">The scope being requested.</param>
    /// <param name="now">Current UTC time.</param>
    /// <returns>True if the token authorises the scope at the given time.</returns>
    public bool IsValidFor(ConsentScope scope, DateTimeOffset now) =>
        Scopes.Contains(scope) && now < ExpiresAt;
}
