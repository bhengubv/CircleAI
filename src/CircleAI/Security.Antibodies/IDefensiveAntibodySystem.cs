// IDefensiveAntibodySystem.cs
//
// The ONE entry point for running an antibody. Hosts resolve this and nothing else
// as the run path. Every method here goes through the authorized-use gate before it
// touches a capability — that is what guarantees "every capability behind the gate".

using CircleAI.Security.Antibodies.Awareness;
using CircleAI.Security.Antibodies.Gate;

namespace CircleAI.Security.Antibodies;

/// <summary>
/// The defensive antibody run path. Each method requires a defined
/// <see cref="DefensiveThreatContext"/> and passes through the
/// <see cref="IAuthorizedUseGate"/> before any assessment runs. A denied request
/// yields a <see cref="ThreatAwarenessResult"/> with
/// <see cref="ThreatAwarenessResult.WasAuthorized"/> = <c>false</c> and nothing is
/// assessed. Every result is advisory — the surface has no action-taking methods.
/// </summary>
public interface IDefensiveAntibodySystem
{
    /// <summary>
    /// Warns the user about a file they are about to open, if a defined threat
    /// justifies the check and the gate authorizes it.
    /// </summary>
    /// <param name="artifact">The file (by hash) to assess.</param>
    /// <param name="threat">The defined threat that justifies running the antibody.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ThreatAwarenessResult> AssessFileAsync(
        FileArtifact artifact, DefensiveThreatContext threat, CancellationToken ct = default);

    /// <summary>
    /// Warns the user about a URL / IP / domain they are about to connect to, if a
    /// defined threat justifies the check and the gate authorizes it.
    /// </summary>
    /// <param name="indicator">The network location to assess.</param>
    /// <param name="threat">The defined threat that justifies running the antibody.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ThreatAwarenessResult> AssessNetworkIndicatorAsync(
        NetworkIndicator indicator, DefensiveThreatContext threat, CancellationToken ct = default);

    /// <summary>
    /// Checks the user's OWN identity for breach exposure so they can rotate an
    /// exposed credential, if a defined threat justifies the check and the gate
    /// authorizes it. Only ever concerns the user's own identity.
    /// </summary>
    /// <param name="identity">The user's own identity value to assess.</param>
    /// <param name="threat">The defined threat that justifies running the antibody.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ThreatAwarenessResult> AssessOwnIdentityExposureAsync(
        IdentityIndicator identity, DefensiveThreatContext threat, CancellationToken ct = default);
}
