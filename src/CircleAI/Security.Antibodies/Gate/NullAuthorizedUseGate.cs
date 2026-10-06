// NullAuthorizedUseGate.cs
//
// The shipped default gate. It DENIES EVERY REQUEST. This is what makes the whole
// antibody subsystem deny-by-default: with no host configuration, nothing can run.
// "Absence of configuration is absence of permission. Silence is denial."

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// The default <see cref="IAuthorizedUseGate"/>: it denies <b>every</b> request,
/// unconditionally. This is the deny-by-default posture required by
/// <c>docs/SECURITY_AUTHORIZED_USE.md</c>. It is the gate a
/// <see cref="DefensiveAntibodySystem"/> uses unless a host deliberately supplies
/// one that can grant.
/// </summary>
/// <remarks>
/// Verification: pure, deterministic, no state, no I/O — trivially correct and safe
/// to share. Use <see cref="Instance"/>.
/// </remarks>
public sealed class NullAuthorizedUseGate : IAuthorizedUseGate
{
    /// <summary>The reason attached to every denial from this gate.</summary>
    public const string DenialReason =
        "No authorized-use gate is configured. Antibodies are denied by default; " +
        "a host must explicitly wire a gate that can grant before any antibody can run.";

    /// <summary>Shared singleton — this gate is stateless.</summary>
    public static NullAuthorizedUseGate Instance { get; } = new();

    /// <summary>Prefer <see cref="Instance"/>; the constructor is public only for DI containers.</summary>
    public NullAuthorizedUseGate() { }

    /// <inheritdoc/>
    public ValueTask<AuthorizationDecision> RequestAuthorizationAsync(
        AuthorizedUseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(AuthorizationDecision.Deny(request, DenialReason));
    }
}
