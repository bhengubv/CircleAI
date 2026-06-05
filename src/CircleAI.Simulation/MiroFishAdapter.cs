using System.Diagnostics.CodeAnalysis;
using CircleAI.Core.Validation;

namespace CircleAI.Simulation;

/// <summary>
/// Adapter for the MiroFish GraphRAG simulation engine.
/// When a real MiroFish engine is registered it is preferred;
/// otherwise falls back to <see cref="LocalSimulationEngine"/>.
/// </summary>
/// <remarks>
/// Marked <see cref="VerificationLevel.Reference"/>: the fall-back local
/// engine is deterministic and tested, but no real MiroFish engine has yet
/// been wired through this adapter in a production run.
/// </remarks>
[Experimental("CIRCLEAI_SIM_001", UrlFormat = "https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md#{0}")]
[CircleAIVerificationStatus(VerificationLevel.Reference)]
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
