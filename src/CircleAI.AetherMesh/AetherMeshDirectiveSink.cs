// ──────────────────────────────────────────────────────────────────────────
// AetherMeshDirectiveSink
//
// Implements CircleAI.Aether.ISecurityDirectiveConsumer (CircleAI's side)
// AND forwards every received directive to the AetherMesh policy engine
// via its own AetherMesh.Extensibility.ISecurityDirectiveConsumer.
//
// In effect: when BhenguAI in CircleAI issues a SecurityDirective, it
// crosses this sink and lands on AetherMesh's policy engine, which decides
// whether to honour it (per AetherMesh deployment policy).
// ──────────────────────────────────────────────────────────────────────────

using CircleAI.Aether;
using MeshDirective = AetherMesh.Extensibility.SecurityDirective;
using MeshConsumer = AetherMesh.Extensibility.ISecurityDirectiveConsumer;

namespace CircleAI.AetherMesh;

/// <summary>
/// Forwards CircleAI security directives to the AetherMesh policy engine.
/// Implements CircleAI's <see cref="ISecurityDirectiveConsumer"/> so it can
/// be registered as a directive sink on the CircleAI side.
/// </summary>
public sealed class AetherMeshDirectiveSink : ISecurityDirectiveConsumer
{
    private readonly MeshConsumer _meshConsumer;

    public AetherMeshDirectiveSink(MeshConsumer meshConsumer)
    {
        ArgumentNullException.ThrowIfNull(meshConsumer);
        _meshConsumer = meshConsumer;
    }

    /// <summary>
    /// Receives a CircleAI directive, translates it into the AetherMesh
    /// shape, and hands it to the AetherMesh policy engine. Whether the
    /// directive is honoured is the policy engine's decision.
    /// </summary>
    public void OnDirective(SecurityDirective directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        var meshDirective = new MeshDirective(
            EventTranslator.MapDirectiveKind(directive.Kind),
            directive.TargetNodeId,
            directive.TrustScoreOverride,
            EventTranslator.MapThreatLevel(directive.ThreatLevel),
            directive.Reason,
            directive.Duration,
            directive.IssuedAt);

        _meshConsumer.OnDirective(meshDirective);
    }
}
