// ──────────────────────────────────────────────────────────────────────────
// AetherNetInboundDirectiveBridge
//
// Inverse of AetherNetDirectiveSink.
//
//   AetherNetDirectiveSink         CircleAI → AetherNet (outbound)
//   AetherNetInboundDirectiveBridge AetherNet → CircleAI (INBOUND, this file)
//
// When the live AetherNet runtime publishes a SecurityDirective — either
// authored locally by another consumer or received from a peer over the
// mesh — this bridge translates it into the CircleAI.Aether shape and
// forwards it to the registered CircleAI consumer (typically
// MeshDirectiveStore in CircleAI.Security.AetherNet).
//
// Registered on the AetherNet side as an additional ISecurityDirectiveConsumer
// (the mesh runtime calls every registered consumer when a directive
// arrives).
// ──────────────────────────────────────────────────────────────────────────

using CircleAI.Aether;
using MeshDirective = AetherNet.Extensibility.SecurityDirective;
using MeshConsumer  = AetherNet.Extensibility.ISecurityDirectiveConsumer;

namespace CircleAI.AetherNet;

/// <summary>
/// Receives AetherNet-side <see cref="MeshDirective"/>s and forwards them
/// into CircleAI's <see cref="ISecurityDirectiveConsumer"/>. The other
/// half of the bidirectional directive pipeline:
/// <see cref="AetherNetDirectiveSink"/> handles outbound, this handles inbound.
/// </summary>
public sealed class AetherNetInboundDirectiveBridge : MeshConsumer
{
    private readonly ISecurityDirectiveConsumer _circleConsumer;

    public AetherNetInboundDirectiveBridge(ISecurityDirectiveConsumer circleConsumer)
    {
        ArgumentNullException.ThrowIfNull(circleConsumer);
        _circleConsumer = circleConsumer;
    }

    /// <inheritdoc/>
    public void OnDirective(MeshDirective meshDirective)
    {
        ArgumentNullException.ThrowIfNull(meshDirective);

        var circleDirective = new SecurityDirective(
            Kind: EventTranslator.MapDirectiveKind(meshDirective.Kind),
            TargetNodeId: meshDirective.TargetNodeId,
            TrustScoreOverride: meshDirective.TrustScoreOverride,
            ThreatLevel: EventTranslator.MapThreatLevel(meshDirective.ThreatLevel),
            Reason: meshDirective.Reason,
            Duration: meshDirective.Duration,
            IssuedAt: meshDirective.IssuedAt);

        _circleConsumer.OnDirective(circleDirective);
    }
}
