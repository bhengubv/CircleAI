namespace Circle.AI.Simulation;

/// <summary>
/// Enumerates the kinds of simulation scenarios supported by the engine.
/// </summary>
public enum ScenarioKind
{
    /// <summary>Model what happens if a configuration key changes.</summary>
    ConfigurationShift,

    /// <summary>Model a new data-sharing pipeline being introduced.</summary>
    DataPipelineChange,

    /// <summary>Model a code deployment propagating through the peer network.</summary>
    SoftwareDeployment,

    /// <summary>Model a security patch propagating through the peer network.</summary>
    SecurityPatch,

    /// <summary>
    /// Model how a confirmed runtime threat (from an <c>AnomalySignal</c>)
    /// would propagate through the peer network if not contained.
    /// Built by <see cref="ThreatPropagationScenario.FromAnomalySignal"/>.
    /// </summary>
    ThreatPropagation,
}

/// <summary>
/// Describes a single simulation scenario, including its kind, parameters,
/// and the number of simulation steps to run.
/// </summary>
public sealed record SimulationScenario(
    Guid         Id,
    ScenarioKind Kind,
    string       Description,
    IReadOnlyDictionary<string, string> Parameters,  // scenario-specific config
    int          StepCount,                          // simulation depth, default 10
    DateTimeOffset CreatedAt
)
{
    /// <summary>
    /// Creates a new <see cref="SimulationScenario"/> with a generated ID and the current UTC timestamp.
    /// </summary>
    /// <param name="kind">The kind of scenario to simulate.</param>
    /// <param name="description">A human-readable description of the scenario.</param>
    /// <param name="parameters">Optional scenario-specific configuration key-value pairs.</param>
    /// <param name="steps">Number of diffusion steps; defaults to 10.</param>
    /// <returns>A new <see cref="SimulationScenario"/>.</returns>
    public static SimulationScenario Create(ScenarioKind kind, string description,
        IReadOnlyDictionary<string, string>? parameters = null, int steps = 10) =>
        new(Guid.NewGuid(), kind, description,
            parameters ?? new Dictionary<string, string>(),
            steps, DateTimeOffset.UtcNow);
}
