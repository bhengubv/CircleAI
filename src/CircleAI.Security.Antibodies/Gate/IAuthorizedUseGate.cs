// IAuthorizedUseGate.cs
//
// THE boundary. Nothing in this library runs an antibody without a granted
// decision from an IAuthorizedUseGate. The shipped default (NullAuthorizedUseGate)
// denies everything, so the whole subsystem is deny-by-default until a host
// deliberately wires a gate that can grant.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// The authorized-use gate that must be explicitly satisfied before any antibody
/// runs. It is the single chokepoint that makes the boundary in
/// <c>docs/SECURITY_AUTHORIZED_USE.md</c> true at runtime.
/// </summary>
/// <remarks>
/// Implementations must be <b>deny-by-default</b>: return a denied
/// <see cref="AuthorizationDecision"/> unless there is an explicit, current reason
/// to grant. The shipped default is <see cref="NullAuthorizedUseGate"/>, which
/// denies every request. A host opts in to antibodies by supplying a gate that can
/// grant (for example <see cref="ExplicitConsentAuthorizedUseGate"/>), never by the
/// library relaxing its own default.
/// </remarks>
public interface IAuthorizedUseGate
{
    /// <summary>
    /// Evaluates <paramref name="request"/> and returns the decision. A caller must
    /// treat any non-<see cref="AuthorizationDecision.Granted"/> result as an
    /// absolute refusal and must not run the capability.
    /// </summary>
    /// <param name="request">The capability + defined threat being requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A granted or denied <see cref="AuthorizationDecision"/>.</returns>
    ValueTask<AuthorizationDecision> RequestAuthorizationAsync(
        AuthorizedUseRequest request,
        CancellationToken ct = default);
}
