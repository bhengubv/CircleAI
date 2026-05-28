// SecurityBridgeTests.cs
//
// Tests for the Orchestration ↔ Security integration:
//   - IncidentTrigger.FromAnomalySignal (priority derivation, threshold gate)
//   - SecurityOrchestrationBridge (parallel watchdog + agent dispatch)

using CircleAI.Orchestration;
using CircleAI.Security;
using Xunit;

namespace CircleAI.Orchestration.Tests;

// ── IncidentTrigger.FromAnomalySignal ────────────────────────────────────────

public sealed class IncidentTriggerAnomalyTests
{
    [Fact]
    public void FromAnomalySignal_BelowThreshold_ReturnsNull()
    {
        var signal = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.10, "M", "low conf");
        var task = IncidentTrigger.FromAnomalySignal(signal);
        Assert.Null(task);
    }

    [Fact]
    public void FromAnomalySignal_LowConfidence_ProducesNormalPriority()
    {
        var signal = AnomalySignal.Create(ThreatVector.AgentPatchRejected, 0.35, "M", "d");
        var task = IncidentTrigger.FromAnomalySignal(signal);
        Assert.NotNull(task);
        Assert.Equal(AgentPriority.Normal, task!.Priority);
    }

    [Fact]
    public void FromAnomalySignal_HighConfidence_ProducesHighPriority()
    {
        var signal = AnomalySignal.Create(ThreatVector.AgentPatchRejected, 0.70, "M", "d");
        var task = IncidentTrigger.FromAnomalySignal(signal);
        Assert.NotNull(task);
        Assert.Equal(AgentPriority.High, task!.Priority);
    }

    [Fact]
    public void FromAnomalySignal_VeryHighConfidence_ProducesCriticalPriority()
    {
        var signal = AnomalySignal.Create(ThreatVector.BiometricSpoofAttempt, 0.95, "M", "d");
        var task = IncidentTrigger.FromAnomalySignal(signal);
        Assert.NotNull(task);
        Assert.Equal(AgentPriority.Critical, task!.Priority);
    }

    [Fact]
    public void FromAnomalySignal_HighSeverityVector_BumpsPriorityOneRank()
    {
        // 0.70 confidence on a high-severity vector should bump High → Critical
        var signal = AnomalySignal.Create(ThreatVector.PrivilegeEscalation, 0.70, "M", "d");
        var task = IncidentTrigger.FromAnomalySignal(signal);
        Assert.NotNull(task);
        Assert.Equal(AgentPriority.Critical, task!.Priority);
    }

    [Fact]
    public void FromAnomalySignal_AlwaysProducesSecurityRole()
    {
        var signal = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.50, "M", "d");
        var task = IncidentTrigger.FromAnomalySignal(signal);
        Assert.NotNull(task);
        Assert.Equal(AgentRole.Security, task!.Role);
    }

    [Fact]
    public void FromAnomalySignal_IncludesSignalIdAndEvidenceInInputs()
    {
        var evidence = new Dictionary<string, string> { ["hash"] = "abc123" };
        var signal = AnomalySignal.Create(
            ThreatVector.StateCorruption, 0.80, "Companion", "state mutation", evidence);

        var task = IncidentTrigger.FromAnomalySignal(signal);

        Assert.NotNull(task);
        Assert.Equal(signal.Id.ToString(), task!.Inputs["signal_id"]);
        Assert.Equal("StateCorruption", task.Inputs["vector"]);
        Assert.Equal("Companion", task.Inputs["affected_module"]);
        Assert.Equal("abc123", task.Inputs["hash"]); // evidence merged
    }

    [Fact]
    public void FromAnomalySignal_NullSignal_Throws() =>
        Assert.Throws<ArgumentNullException>(() => IncidentTrigger.FromAnomalySignal(null!));
}

// ── SecurityOrchestrationBridge ──────────────────────────────────────────────

public sealed class SecurityOrchestrationBridgeTests
{
    [Fact]
    public async Task OnAnomalyDetected_DelegatesToInnerWatchdog()
    {
        var inner = new DefaultSecurityWatchdog();
        using var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Security,
            (task, _) => Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed, "ok",
                Array.Empty<string>(), DateTimeOffset.UtcNow)));

        var orchestrator = new LokiOrchestrator(dispatcher);
        var bridge = new SecurityOrchestrationBridge(inner, orchestrator);

        var signal = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.45, "M", "d");
        var response = await bridge.OnAnomalyDetectedAsync(signal);

        // Mid-confidence MemoryAnomaly → KeyRotation from DefaultSecurityWatchdog
        Assert.Equal(SecurityResponseKind.KeyRotation, response.Kind);
    }

    [Fact]
    public async Task OnAnomalyDetected_HighConfidence_DispatchesSecurityAgent()
    {
        var agentInvoked = new TaskCompletionSource<AgentTask>();
        var inner = new DefaultSecurityWatchdog();
        using var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Security, (task, _) =>
        {
            agentInvoked.TrySetResult(task);
            return Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed, "ok",
                Array.Empty<string>(), DateTimeOffset.UtcNow));
        });

        var orchestrator = new LokiOrchestrator(dispatcher);
        var bridge = new SecurityOrchestrationBridge(inner, orchestrator);

        var signal = AnomalySignal.Create(ThreatVector.ControlFlowDrift, 0.95, "M", "drift");
        await bridge.OnAnomalyDetectedAsync(signal);

        // Wait up to 2 s for the parallel agent dispatch
        var dispatched = await Task.WhenAny(agentInvoked.Task, Task.Delay(2_000));
        Assert.Same(agentInvoked.Task, dispatched);

        var task = await agentInvoked.Task;
        Assert.Equal(AgentRole.Security, task.Role);
        Assert.Equal(signal.Id.ToString(), task.Inputs["signal_id"]);
    }

    [Fact]
    public async Task OnAnomalyDetected_BelowThreshold_DoesNotDispatchAgent()
    {
        var agentInvoked = false;
        var inner = new DefaultSecurityWatchdog();
        using var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Security, (task, _) =>
        {
            agentInvoked = true;
            return Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed, "ok",
                Array.Empty<string>(), DateTimeOffset.UtcNow));
        });

        var orchestrator = new LokiOrchestrator(dispatcher);
        var bridge = new SecurityOrchestrationBridge(inner, orchestrator);

        var signal = AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.10, "M", "low");
        await bridge.OnAnomalyDetectedAsync(signal);

        // Give the parallel path time to (not) dispatch
        await Task.Delay(200);
        Assert.False(agentInvoked);
    }

    [Fact]
    public async Task OnAnomalyDetected_StreamPropagates()
    {
        var inner = new DefaultSecurityWatchdog();
        using var dispatcher = new LocalAgentDispatcher();
        var orchestrator = new LokiOrchestrator(dispatcher);
        var bridge = new SecurityOrchestrationBridge(inner, orchestrator);

        var streamTask = Task.Run(async () =>
        {
            await using var enumerator = bridge.StreamSignalsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            return enumerator.Current;
        });

        // Give the stream subscription a moment to attach
        await Task.Delay(50);

        var signal = AnomalySignal.Create(ThreatVector.NetworkPivot, 0.75, "M", "pivot");
        await bridge.OnAnomalyDetectedAsync(signal);

        var streamed = await streamTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(signal.Id, streamed.Id);
    }
}
