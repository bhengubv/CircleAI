// ThreatPropagationScenarioTests.cs
//
// Tests for the Simulation ↔ Security integration via ThreatPropagationScenario.

using CircleAI.Security;
using CircleAI.Simulation;
using Xunit;

namespace CircleAI.Simulation.Tests;

public sealed class ThreatPropagationScenarioTests
{
    [Fact]
    public void FromAnomalySignal_ProducesThreatPropagationKind()
    {
        var signal = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.7, "M", "d");
        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal);
        Assert.Equal(ScenarioKind.ThreatPropagation, scenario.Kind);
    }

    [Fact]
    public void FromAnomalySignal_NetworkPivot_Uses30Steps()
    {
        var signal = AnomalySignal.Create(ThreatVector.NetworkPivot, 0.9, "M", "d");
        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal);
        Assert.Equal(30, scenario.StepCount);
    }

    [Fact]
    public void FromAnomalySignal_BiometricSpoof_Uses12Steps()
    {
        var signal = AnomalySignal.Create(ThreatVector.BiometricSpoofAttempt, 0.9, "M", "d");
        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal);
        Assert.Equal(12, scenario.StepCount);
    }

    [Fact]
    public void FromAnomalySignal_StepOverride_TakesPrecedence()
    {
        var signal = AnomalySignal.Create(ThreatVector.NetworkPivot, 0.9, "M", "d");
        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal, stepOverride: 100);
        Assert.Equal(100, scenario.StepCount);
    }

    [Fact]
    public void FromAnomalySignal_ParametersIncludeSignalIdAndVector()
    {
        var signal = AnomalySignal.Create(ThreatVector.ControlFlowDrift, 0.85, "Comp", "drift");
        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal);

        Assert.Equal(signal.Id.ToString(), scenario.Parameters["signal_id"]);
        Assert.Equal("ControlFlowDrift", scenario.Parameters["vector"]);
        Assert.Equal("Comp", scenario.Parameters["affected_module"]);
    }

    [Fact]
    public void FromAnomalySignal_EvidenceIsMergedIntoParameters()
    {
        var evidence = new Dictionary<string, string> { ["hash"] = "abc123" };
        var signal = AnomalySignal.Create(
            ThreatVector.StateCorruption, 0.8, "M", "d", evidence);

        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal);

        Assert.Equal("abc123", scenario.Parameters["hash"]);
    }

    [Fact]
    public async Task FromAnomalySignal_FeedsNetworkHealthSimulator()
    {
        var sim = new NetworkHealthSimulator();
        var signal = AnomalySignal.Create(ThreatVector.NetworkPivot, 0.9, "M", "pivot");
        var scenario = ThreatPropagationScenario.FromAnomalySignal(signal);

        // Empty history → graph has no edges, so simulator reports Healthy.
        var result = await sim.ForecastAsync(
            history: Array.Empty<CircleAI.Memory.EpisodicMemoryEntry>(),
            scenario: scenario);

        Assert.Equal(scenario.Id, result.ScenarioId);
        Assert.Equal(30, result.StepsRun);
    }

    [Fact]
    public void FromAnomalySignal_NullSignal_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            ThreatPropagationScenario.FromAnomalySignal(null!));
}
