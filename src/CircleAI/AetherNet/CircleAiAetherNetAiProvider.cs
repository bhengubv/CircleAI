// ──────────────────────────────────────────────────────────────────────────
// CircleAiAetherNetAiProvider
//
// Plugs CircleAI's brain (IAetherIntelligence) into AetherNet's
// IAetherNetAiProvider extension seat. AetherNet's routing layer asks
// for route advice, threat assessments, and network health; we forward
// each call to CircleAI's intelligence surface and translate the result.
//
// What CircleAI doesn't yet produce (transport biases, structured route
// suggestions) returns a sensible default that lets the mesh fall back to
// its own logic — no false claim of intelligence we don't have.
// ──────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Extensibility;
using AetherNet.Protocol;
using CircleAI.Aether;

namespace CircleAI.AetherNet;

/// <summary>
/// Bridges CircleAI's <see cref="IAetherIntelligence"/> to AetherNet's
/// <see cref="IAetherNetAiProvider"/> extension seat.
/// </summary>
public sealed class CircleAiAetherNetAiProvider : IAetherNetAiProvider
{
    private readonly IAetherIntelligence _intelligence;
    private static readonly IReadOnlyDictionary<string, double> _emptyBiases =
        new Dictionary<string, double>();
    private static readonly IReadOnlyList<AiRouteSuggestion> _emptyRoutes =
        new List<AiRouteSuggestion>();

    public CircleAiAetherNetAiProvider(IAetherIntelligence intelligence)
    {
        ArgumentNullException.ThrowIfNull(intelligence);
        _intelligence = intelligence;
    }

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AiRouteSuggestion>> SuggestRoutesAsync(
        string destinationUhid, int payloadBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationUhid))
            return _emptyRoutes;

        var advice = await _intelligence
            .GetRoutingAdviceAsync(destinationUhid, cancellationToken)
            .ConfigureAwait(false);

        if (advice is null || advice.RecommendedPath.Count == 0)
            return _emptyRoutes;

        return new[] { new AiRouteSuggestion(advice.RecommendedPath, advice.Confidence) };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// CircleAI does not yet model per-transport biases. Returning an empty
    /// dictionary tells AetherNet to use its built-in transport selector
    /// without AI adjustment — the correct fallback when no signal exists.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, double>> GetTransportBiasesAsync(
        int payloadBytes, CancellationToken cancellationToken)
        => Task.FromResult(_emptyBiases);

    /// <inheritdoc/>
    public async Task<AiThreatLevel> AssessThreatAsync(
        MeshPacket packet, CancellationToken cancellationToken)
    {
        if (packet is null || string.IsNullOrWhiteSpace(packet.SourceUhid))
            return AiThreatLevel.None;

        var assessment = await _intelligence
            .AssessThreatAsync(packet.SourceUhid, cancellationToken)
            .ConfigureAwait(false);

        return MapToMeshThreatLevel(assessment.Level);
    }

    /// <inheritdoc/>
    public async Task<AiNetworkHealthReport> GetNetworkHealthAsync(CancellationToken cancellationToken)
    {
        var health = await _intelligence
            .GetNetworkHealthAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AiNetworkHealthReport(
            health.OverallScore,
            health.TrustedNodeCount,
            health.SuspiciousNodeCount,
            health.Summary,
            health.GeneratedAt);
    }

    // AetherNet's AiThreatLevel only has 4 values (None, Low, Medium, High).
    // CircleAI's AetherThreatLevel has Critical. Fold Critical → High because
    // that's the strongest signal AetherNet's AI seat can carry.
    private static AiThreatLevel MapToMeshThreatLevel(AetherThreatLevel l) => l switch
    {
        AetherThreatLevel.None      => AiThreatLevel.None,
        AetherThreatLevel.Low       => AiThreatLevel.Low,
        AetherThreatLevel.Medium    => AiThreatLevel.Medium,
        AetherThreatLevel.High      => AiThreatLevel.High,
        AetherThreatLevel.Critical  => AiThreatLevel.High,
        _ => AiThreatLevel.None,
    };
}
