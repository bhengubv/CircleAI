// IAuthorizedUseConsentStore.cs
//
// Where ExplicitConsentAuthorizedUseGate looks for a live consent. The store holds
// only what a host deliberately put there; an empty store therefore grants nothing.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// Supplies the explicit consents an <see cref="ExplicitConsentAuthorizedUseGate"/>
/// consults. A host populates it when a human authorizes a capability; the gate
/// only ever reads from it.
/// </summary>
public interface IAuthorizedUseConsentStore
{
    /// <summary>
    /// Returns an active <see cref="AuthorizedUseConsent"/> for
    /// <paramref name="capability"/> as of <paramref name="now"/>, or <c>null</c> if
    /// none exists. Returning <c>null</c> is normal and means "deny".
    /// </summary>
    /// <param name="capability">The capability being requested.</param>
    /// <param name="now">The current time to evaluate validity against.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<AuthorizedUseConsent?> FindActiveConsentAsync(
        AntibodyCapability capability,
        DateTimeOffset now,
        CancellationToken ct = default);
}
