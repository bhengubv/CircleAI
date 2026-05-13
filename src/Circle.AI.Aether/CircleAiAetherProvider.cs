// SPDX-License-Identifier: MIT

using Aether.Extensibility;
using Aether.Protocol;

namespace Circle.AI.Aether;

/// <summary>
/// Implements <see cref="IAetherAiProvider"/> by delegating to CircleAI's
/// <see cref="IAetherIntelligence"/> and <see cref="IAISecurityLayer"/> contracts.
///
/// <para>
/// This class is the bridge between the two systems:
/// </para>
/// <list type="bullet">
///   <item>
///     <see cref="SuggestRoutesAsync"/> maps CircleAI's <see cref="RoutingAdvice"/>
///     to Aether's <see cref="AiRouteSuggestion"/>, letting the AI pre-populate
///     route candidates before AODV floods the mesh.
///   </item>
///   <item>
///     <see cref="AssessThreatAsync"/> maps CircleAI's <see cref="AetherThreatLevel"/>
///     to Aether's <see cref="AiThreatLevel"/>, letting the AI suppress packet
///     forwarding from suspicious senders.
///   </item>
///   <item>
///     <see cref="GetTransportBiasesAsync"/> uses the AI security posture to prefer
///     peer-to-peer transports (BLE, Wi-Fi Direct) when the threat level is elevated —
///     cellular relay routes through a third party and is deprioritised under threat.
///   </item>
/// </list>
///
/// <para>
/// Register in DI as the singleton for <see cref="IAetherAiProvider"/>:
/// <code>
///   services.AddSingleton&lt;IAetherAiProvider&gt;(sp =>
///       new CircleAiAetherProvider(
///           sp.GetRequiredService&lt;IAetherIntelligence&gt;(),
///           sp.GetRequiredService&lt;IAISecurityLayer&gt;()));
/// </code>
/// This one line activates the Obelix mode — full AI-augmented mesh.
/// </para>
/// </summary>
public sealed class CircleAiAetherProvider : IAetherAiProvider
{
    private readonly IAetherIntelligence _intelligence;
    private readonly IAISecurityLayer    _security;

    /// <summary>
    /// Initialises the provider with the CircleAI intelligence and security services.
    /// </summary>
    /// <param name="intelligence">The CircleAI intelligence output surface.</param>
    /// <param name="security">The CircleAI AI security layer.</param>
    public CircleAiAetherProvider(
        IAetherIntelligence intelligence,
        IAISecurityLayer    security)
    {
        ArgumentNullException.ThrowIfNull(intelligence);
        ArgumentNullException.ThrowIfNull(security);
        _intelligence = intelligence;
        _security     = security;
    }

    /// <inheritdoc/>
    /// <remarks>Always <see langword="true"/> — CircleAI is present and operational.</remarks>
    public bool IsAvailable => true;

    // ── Route suggestion ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Calls <see cref="IAetherIntelligence.GetRoutingAdviceAsync"/> and maps the
    /// result to an <see cref="AiRouteSuggestion"/>.
    ///
    /// Nodes listed in <see cref="RoutingAdvice.AvoidNodes"/> are excluded from the
    /// path so AODV will not select them during discovery.  The confidence value from
    /// CircleAI flows through unchanged.
    /// </remarks>
    public async Task<IReadOnlyList<AiRouteSuggestion>> SuggestRoutesAsync(
        string destinationUhid,
        int payloadBytes,
        CancellationToken cancellationToken = default)
    {
        RoutingAdvice advice;
        try
        {
            advice = await _intelligence
                .GetRoutingAdviceAsync(destinationUhid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // CircleAI failures must never block Aether.
            return Array.Empty<AiRouteSuggestion>();
        }

        if (advice.RecommendedPath.Count == 0)
            return Array.Empty<AiRouteSuggestion>();

        // Filter out any avoid-listed hops from the recommended path.
        var avoidSet = new HashSet<string>(advice.AvoidNodes, StringComparer.OrdinalIgnoreCase);
        var cleanPath = advice.RecommendedPath
            .Where(hop => !avoidSet.Contains(hop))
            .ToList();

        if (cleanPath.Count == 0)
            return Array.Empty<AiRouteSuggestion>();

        return [new AiRouteSuggestion(cleanPath, advice.Confidence)];
    }

    // ── Transport bias ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Uses the AI security posture to bias transport selection:
    /// <list type="bullet">
    ///   <item>Under elevated threat: BLE and Wi-Fi Direct are preferred (peer-to-peer,
    ///     harder to intercept); HTTP relay is deprioritised (routes through a third party).</item>
    ///   <item>Normal posture: all multipliers are 1.0 (neutral, Kalman scores used as-is).</item>
    /// </list>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, double>> GetTransportBiasesAsync(
        int payloadBytes,
        CancellationToken cancellationToken = default)
    {
        SecurityPosture posture;
        try
        {
            posture = await _security
                .GetPostureAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return new Dictionary<string, double>();
        }

        if (!posture.IsActive || posture.OverallThreatLevel == AetherThreatLevel.None)
            return new Dictionary<string, double>();

        // Under any non-None threat level, bias P2P transports up and relay down.
        double p2pBoost    = posture.OverallThreatLevel >= AetherThreatLevel.High  ? 3.0 :
                             posture.OverallThreatLevel >= AetherThreatLevel.Medium ? 2.0 : 1.5;
        double relayPenalty = posture.OverallThreatLevel >= AetherThreatLevel.High  ? 0.1 :
                              posture.OverallThreatLevel >= AetherThreatLevel.Medium ? 0.3 : 0.6;

        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // Aether Blue (BLE) — peer-to-peer, preferred under threat.
            ["Aether Blue (BLE)"]            = p2pBoost,
            // Aether Green (Wi-Fi Direct) — peer-to-peer, preferred under threat.
            ["Aether Green (Wi-Fi Direct)"]  = p2pBoost,
            // Aether Teal (NearLink) — peer-to-peer, preferred under threat.
            ["Aether Teal (NearLink)"]       = p2pBoost,
            // Aether Purple (HTTP relay) — routes through a server; deprioritised.
            ["Aether Purple (HTTP Relay)"]   = relayPenalty,
        };
    }

    // ── Threat assessment ────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to <see cref="IAetherIntelligence.AssessThreatAsync"/> using the
    /// packet's source UHID.  CircleAI's five-level <see cref="AetherThreatLevel"/>
    /// maps to Aether's four-level <see cref="AiThreatLevel"/> with <c>Critical</c>
    /// folding into <see cref="AiThreatLevel.High"/>.
    /// </remarks>
    public async Task<AiThreatLevel> AssessThreatAsync(
        MeshPacket packet,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packet.SourceUhid))
            return AiThreatLevel.None;

        ThreatAssessment assessment;
        try
        {
            assessment = await _intelligence
                .AssessThreatAsync(packet.SourceUhid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return AiThreatLevel.None;
        }

        return MapThreatLevel(assessment.Level);
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps CircleAI's five-level <see cref="AetherThreatLevel"/> to Aether's
    /// four-level <see cref="AiThreatLevel"/>. <c>Critical</c> folds into
    /// <see cref="AiThreatLevel.High"/> — both suppress forwarding.
    /// </summary>
    private static AiThreatLevel MapThreatLevel(AetherThreatLevel level) => level switch
    {
        AetherThreatLevel.None     => AiThreatLevel.None,
        AetherThreatLevel.Low      => AiThreatLevel.Low,
        AetherThreatLevel.Medium   => AiThreatLevel.Medium,
        AetherThreatLevel.High     => AiThreatLevel.High,
        AetherThreatLevel.Critical => AiThreatLevel.High,   // Critical → High (both suppress forwarding)
        _                          => AiThreatLevel.None,
    };
}
