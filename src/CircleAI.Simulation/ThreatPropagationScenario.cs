// ThreatPropagationScenario.cs
//
// Factory that maps a CircleAI.Security AnomalySignal into a
// SimulationScenario that NetworkHealthSimulator can run to forecast
// how the threat would spread through the peer network if not contained.
//
// This is the Simulation ↔ Security integration point. It lives in
// CircleAI.Simulation so that the SDK's simulation surface stays
// Security-aware without Security needing to know about Simulation.

using CircleAI.Security;

namespace CircleAI.Simulation;

/// <summary>
/// Factory for building <see cref="SimulationScenario"/> instances of
/// <see cref="ScenarioKind.ThreatPropagation"/> from an
/// <see cref="AnomalySignal"/>.
/// </summary>
public static class ThreatPropagationScenario
{
    /// <summary>
    /// Number of diffusion steps the simulator should run for a given
    /// <see cref="ThreatVector"/>. Higher-severity vectors warrant deeper
    /// simulation depth to surface long-range pivot risk.
    /// </summary>
    private static int StepCountFor(ThreatVector vector) => vector switch
    {
        ThreatVector.NetworkPivot          => 30,
        ThreatVector.ControlFlowDrift      => 25,
        ThreatVector.PrivilegeEscalation   => 25,
        ThreatVector.StateCorruption       => 20,
        ThreatVector.MemoryAnomaly         => 15,
        ThreatVector.AgentPatchRejected    => 15,
        ThreatVector.BiometricSpoofAttempt => 12,
        _                                  => 10,
    };

    /// <summary>
    /// Creates a <see cref="SimulationScenario"/> describing how the threat
    /// described by <paramref name="signal"/> would propagate through the
    /// peer network if unmitigated.
    /// </summary>
    /// <param name="signal">
    /// The confirmed anomaly to model. Higher <see cref="AnomalySignal.Confidence"/>
    /// values produce more aggressive simulation parameters.
    /// </param>
    /// <param name="stepOverride">
    /// Optional explicit step count. When <c>null</c> the step count is derived
    /// from the threat vector via <see cref="StepCountFor"/>.
    /// </param>
    /// <returns>A new <see cref="SimulationScenario"/>.</returns>
    public static SimulationScenario FromAnomalySignal(
        AnomalySignal signal,
        int? stepOverride = null)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var parameters = new Dictionary<string, string>(signal.Evidence)
        {
            ["signal_id"]       = signal.Id.ToString(),
            ["vector"]          = signal.Vector.ToString(),
            ["confidence"]      = signal.Confidence.ToString(
                                       "F3", System.Globalization.CultureInfo.InvariantCulture),
            ["affected_module"] = signal.AffectedModule,
            ["detected_at"]     = signal.DetectedAt.ToString("O"),
        };

        int steps = stepOverride ?? StepCountFor(signal.Vector);

        return new SimulationScenario(
            Id          : Guid.NewGuid(),
            Kind        : ScenarioKind.ThreatPropagation,
            Description :
                $"threat-propagation: {signal.Vector} in {signal.AffectedModule} " +
                $"(confidence {signal.Confidence:P0})",
            Parameters  : parameters,
            StepCount   : steps,
            CreatedAt   : DateTimeOffset.UtcNow);
    }
}
