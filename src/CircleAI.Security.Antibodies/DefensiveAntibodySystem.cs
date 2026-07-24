// DefensiveAntibodySystem.cs
//
// The gated facade. It is the only place an antibody actually runs, and every path
// through it does the same thing first: build an AuthorizedUseRequest from the
// caller's defined threat, ask the gate, and — only on a granted decision — call the
// assessor. A denied decision returns a NotAuthorized result WITHOUT touching the
// capability. Structurally, no antibody can run without passing the gate.

using CircleAI.Security.Antibodies.Awareness;
using CircleAI.Security.Antibodies.Gate;

namespace CircleAI.Security.Antibodies;

/// <summary>
/// Default <see cref="IDefensiveAntibodySystem"/>. Composes the authorized-use gate
/// with the three defensive assessors and enforces the gate on every call.
/// </summary>
/// <remarks>
/// Deny-by-default: constructed via <see cref="CreateDenyByDefault"/> it uses
/// <see cref="NullAuthorizedUseGate"/> (denies everything) over
/// <see cref="EmptyIndicatorCorpus"/> (holds nothing), so it is completely inert
/// until a host supplies a gate that can grant and a populated corpus via
/// <see cref="Create"/>. Verification: the gate check precedes every assessor call
/// with no branch that skips it; a denied decision returns before the assessor is
/// referenced.
/// </remarks>
public sealed class DefensiveAntibodySystem : IDefensiveAntibodySystem
{
    private const string FileJustification =
        "Warn the user before they open a file implicated by a defined threat.";
    private const string NetworkJustification =
        "Warn the user before they connect to a location implicated by a defined threat.";
    private const string IdentityJustification =
        "Warn the user if their own identity is exposed, under a defined threat.";

    private readonly IAuthorizedUseGate _gate;
    private readonly IFileThreatAwareness _file;
    private readonly INetworkThreatAwareness _network;
    private readonly IBreachExposureAwareness _breach;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Composes the system from an explicit gate and the three assessors. Prefer the
    /// static factories unless you are wiring custom assessors.
    /// </summary>
    public DefensiveAntibodySystem(
        IAuthorizedUseGate gate,
        IFileThreatAwareness file,
        INetworkThreatAwareness network,
        IBreachExposureAwareness breach,
        TimeProvider? timeProvider = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _breach = breach ?? throw new ArgumentNullException(nameof(breach));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds the fully deny-by-default system: the <see cref="NullAuthorizedUseGate"/>
    /// over an <see cref="EmptyIndicatorCorpus"/>. It denies every request and holds no
    /// threat data — the correct shipped default.
    /// </summary>
    public static DefensiveAntibodySystem CreateDenyByDefault(TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        ILocalIndicatorCorpus corpus = EmptyIndicatorCorpus.Instance;
        return new DefensiveAntibodySystem(
            NullAuthorizedUseGate.Instance,
            new FileThreatAwarenessAssessor(corpus, clock),
            new NetworkThreatAwarenessAssessor(corpus, clock),
            new BreachExposureAssessor(corpus, clock),
            clock);
    }

    /// <summary>
    /// Builds a system over a host-supplied gate and local corpus. The host opts in by
    /// providing a gate that can grant (e.g. <see cref="ExplicitConsentAuthorizedUseGate"/>)
    /// and a populated <see cref="ILocalIndicatorCorpus"/>. Even so, it stays
    /// deny-by-default until the gate actually grants.
    /// </summary>
    public static DefensiveAntibodySystem Create(
        IAuthorizedUseGate gate, ILocalIndicatorCorpus corpus, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(corpus);
        var clock = timeProvider ?? TimeProvider.System;
        return new DefensiveAntibodySystem(
            gate,
            new FileThreatAwarenessAssessor(corpus, clock),
            new NetworkThreatAwarenessAssessor(corpus, clock),
            new BreachExposureAssessor(corpus, clock),
            clock);
    }

    /// <inheritdoc/>
    public async Task<ThreatAwarenessResult> AssessFileAsync(
        FileArtifact artifact, DefensiveThreatContext threat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        AuthorizationDecision decision =
            await AuthorizeAsync(AntibodyCapability.FileReputationAwareness, threat, FileJustification, ct)
                .ConfigureAwait(false);

        if (!decision.Granted)
            return ThreatAwarenessResult.NotAuthorized(IndicatorKind.FileHashSha256, decision.Reason, _clock);

        return await _file.InspectAsync(artifact, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ThreatAwarenessResult> AssessNetworkIndicatorAsync(
        NetworkIndicator indicator, DefensiveThreatContext threat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indicator);

        AuthorizationDecision decision =
            await AuthorizeAsync(AntibodyCapability.NetworkIndicatorAwareness, threat, NetworkJustification, ct)
                .ConfigureAwait(false);

        if (!decision.Granted)
            return ThreatAwarenessResult.NotAuthorized(indicator.Kind, decision.Reason, _clock);

        return await _network.InspectAsync(indicator, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ThreatAwarenessResult> AssessOwnIdentityExposureAsync(
        IdentityIndicator identity, DefensiveThreatContext threat, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        AuthorizationDecision decision =
            await AuthorizeAsync(AntibodyCapability.BreachExposureAwareness, threat, IdentityJustification, ct)
                .ConfigureAwait(false);

        if (!decision.Granted)
            return ThreatAwarenessResult.NotAuthorized(identity.Kind, decision.Reason, _clock);

        return await _breach.InspectAsync(identity, ct).ConfigureAwait(false);
    }

    // Single chokepoint: builds the request from the defined threat and asks the gate.
    // Every public method calls this before referencing an assessor.
    private async ValueTask<AuthorizationDecision> AuthorizeAsync(
        AntibodyCapability capability, DefensiveThreatContext threat, string justification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(threat);
        AuthorizedUseRequest request = AuthorizedUseRequest.For(capability, threat, justification, _clock);
        return await _gate.RequestAuthorizationAsync(request, ct).ConfigureAwait(false);
    }
}
