using Circle.AI.Memory;

namespace Circle.AI.Simulation;

/// <summary>
/// Offline network health simulator. Extracts a knowledge graph from episodic
/// memory, then runs a deterministic diffusion model to forecast the health
/// impact of the given scenario on the peer network.
/// </summary>
public sealed class NetworkHealthSimulator
{
    private readonly IGraphBuilder    _extractor;
    private readonly ISimulationEngine _engine;

    /// <summary>
    /// Initialises the simulator with optional overrides for the graph builder
    /// and simulation engine. Defaults to <see cref="EpisodicGraphExtractor"/>
    /// and <see cref="MiroFishAdapter"/> respectively.
    /// </summary>
    /// <param name="extractor">Optional custom graph builder.</param>
    /// <param name="engine">Optional custom simulation engine.</param>
    public NetworkHealthSimulator(IGraphBuilder? extractor = null, ISimulationEngine? engine = null)
    {
        _extractor = extractor ?? new EpisodicGraphExtractor();
        _engine    = engine    ?? new MiroFishAdapter();
    }

    /// <summary>
    /// Builds a knowledge graph from <paramref name="history"/> and runs the
    /// given <paramref name="scenario"/> through the simulation engine.
    /// </summary>
    /// <param name="history">The episodic memory history to build the graph from.</param>
    /// <param name="scenario">The deployment scenario to forecast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SimulationResult"/> with the forecasted health outcome.</returns>
    public async Task<SimulationResult> ForecastAsync(
        IReadOnlyList<EpisodicMemoryEntry> history,
        SimulationScenario scenario,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(scenario);
        var graph = _extractor.Build(history);
        return await _engine.RunAsync(scenario, graph, ct).ConfigureAwait(false);
    }
}

// ─── Internal default engine ─────────────────────────────────────────────────

/// <summary>
/// Deterministic graph-diffusion engine used when no external MiroFish
/// engine is registered. For internal use only.
/// </summary>
internal sealed class LocalSimulationEngine : ISimulationEngine
{
    private const float DecayPerStep        = 0.01f;
    private const float HighImpactThreshold = 0.7f;

    /// <inheritdoc/>
    public Task<SimulationResult> RunAsync(
        SimulationScenario scenario, KnowledgeGraph graph, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        float health    = 1.0f;
        var   highImpact = new HashSet<string>();

        for (int step = 0; step < scenario.StepCount && health > 0f; step++)
        {
            foreach (var edge in graph.Edges.Values)
            {
                health -= (1f - edge.Weight) * DecayPerStep;

                if (edge.Weight >= HighImpactThreshold
                    && graph.Nodes.TryGetValue(edge.SourceId, out var src))
                    highImpact.Add(src.Label);
            }
            ct.ThrowIfCancellationRequested();
        }

        health = Math.Clamp(health, 0f, 1f);

        var outcome = health switch
        {
            >= 0.8f => SimulationOutcome.Healthy,
            >= 0.5f => SimulationOutcome.Degraded,
            >= 0.2f => SimulationOutcome.Critical,
            _       => SimulationOutcome.Unknown
        };

        var findings = highImpact.Count > 0
            ? (IReadOnlyList<string>)highImpact.Select(l => $"High-impact node detected: {l}").ToList()
            : new List<string> { "No high-impact nodes detected." };

        var recs = outcome is SimulationOutcome.Degraded or SimulationOutcome.Critical
            ? (IReadOnlyList<string>)new List<string>
              {
                  "Review high-weight edges before deployment.",
                  "Consider incremental rollout."
              }
            : new List<string> { "Network health nominal — proceed with deployment." };

        return Task.FromResult(new SimulationResult(
            scenario.Id, outcome, health, findings, recs, scenario.StepCount, DateTimeOffset.UtcNow));
    }
}
