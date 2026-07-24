// AuthorizedUseConsent.cs
//
// An explicit, time-boxed authorization the host records when a human deliberately
// permits a specific antibody capability. This is the ONLY thing that lets
// ExplicitConsentAuthorizedUseGate grant. No consent → deny.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// A recorded, explicit authorization for one <see cref="AntibodyCapability"/>,
/// valid for a bounded window. Represents a deliberate human decision to permit a
/// defensive capability — the "explicit authorized-use" the boundary requires.
/// </summary>
/// <param name="ConsentId">Unique id, quoted in the resulting grant for auditability.</param>
/// <param name="Capability">The single capability this consent authorizes.</param>
/// <param name="GrantedBy">Who authorized it (the human / authority responsible).</param>
/// <param name="Scope">What the consent covers (e.g. the specific threat or subject), for the audit trail.</param>
/// <param name="GrantedAtUtc">When consent was given.</param>
/// <param name="ExpiresAtUtc">When consent lapses. After this it grants nothing.</param>
public sealed record AuthorizedUseConsent(
    Guid ConsentId,
    AntibodyCapability Capability,
    string GrantedBy,
    string Scope,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    /// <summary>
    /// Returns <c>true</c> only if this consent covers <paramref name="capability"/>
    /// and <paramref name="now"/> is within its validity window.
    /// </summary>
    public bool IsActiveFor(AntibodyCapability capability, DateTimeOffset now) =>
        Capability == capability && now >= GrantedAtUtc && now < ExpiresAtUtc;

    /// <summary>
    /// Creates a consent starting now and lasting <paramref name="duration"/>.
    /// Throws if the responsible party or scope is blank, or the duration is not
    /// positive — an unbounded or unattributed consent is not permitted.
    /// </summary>
    public static AuthorizedUseConsent Grant(
        AntibodyCapability capability,
        string grantedBy,
        string scope,
        TimeSpan duration,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Consent duration must be positive.");

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new AuthorizedUseConsent(Guid.NewGuid(), capability, grantedBy, scope, now, now + duration);
    }
}
