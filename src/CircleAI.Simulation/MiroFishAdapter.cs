namespace CircleAI.Simulation;

/// <summary>
/// Adapter for the MiroFish GraphRAG simulation engine.
/// When a real MiroFish engine is registered it is preferred;
/// otherwise falls back to <see cref="LocalSimulationEngine"/>.
/// </summary>
public sealed class MiroFishAdapter : ISimulationEngine
{
    private readonly ISimulationEngine _inner;

    /// <summary>
    /// Initialises the adapter. If <paramref name="externalEngine"/> is
    /// <see langword="null"/> the built-in <see cref="LocalSimulationEngine"/>
    /// is used as the fallback.
    /// </summary>
    /// <param name="externalEngine">
    /// An optional external MiroFish engine implementation.
    /// When <see langword="null"/>, the local fallback engine is used.
    /// </param>
    public MiroFishAdapter(ISimulationEngine? externalEngine = null)
    {
        _inner = externalEngine ?? new LocalSimulationEngine();
    }

    /// <inheritdoc/>
    public Task<SimulationResult> RunAsync(
        SimulationScenario scenario, KnowledgeGraph graph, CancellationToken ct = default) =>
        _inner.RunAsync(scenario, graph, ct);
}
