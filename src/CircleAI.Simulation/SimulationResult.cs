namespace CircleAI.Simulation;

/// <summary>
/// The overall health outcome of a simulation run.
/// </summary>
public enum SimulationOutcome
{
    /// <summary>Health score is 0.8 or above; network is operating normally.</summary>
    Healthy,

    /// <summary>Health score is between 0.5 and 0.8; performance may be reduced.</summary>
    Degraded,

    /// <summary>Health score is between 0.2 and 0.5; service is significantly impaired.</summary>
    Critical,

    /// <summary>Health score is below 0.2; state is indeterminate.</summary>
    Unknown
}

/// <summary>
/// Captures the outcome of a single simulation run, including health score,
/// human-readable findings, and recommended actions.
/// </summary>
public sealed record SimulationResult(
    Guid              ScenarioId,
    SimulationOutcome Outcome,
    float             HealthScore,          // 0.0–1.0; higher = healthier
    IReadOnlyList<string> Findings,         // human-readable simulation findings
    IReadOnlyList<string> Recommendations,
    int               StepsRun,
    DateTimeOffset    CompletedAt
);
