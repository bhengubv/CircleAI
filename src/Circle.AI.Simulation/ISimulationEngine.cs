namespace Circle.AI.Simulation;

/// <summary>
/// Runs a simulation scenario against a knowledge graph and returns a
/// <see cref="SimulationResult"/> describing the predicted health outcome.
/// </summary>
public interface ISimulationEngine
{
    /// <summary>
    /// Executes the simulation asynchronously.
    /// </summary>
    /// <param name="scenario">The scenario to simulate.</param>
    /// <param name="graph">The knowledge graph that provides the network topology.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SimulationResult"/> with the forecasted outcome.</returns>
    Task<SimulationResult> RunAsync(SimulationScenario scenario, KnowledgeGraph graph, CancellationToken ct = default);
}
