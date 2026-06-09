// LokiSemaphoreP3Tests.cs
//
// P3.2 — the orchestrator's semaphore is now disposed and a
// non-cancellation dispatcher exception no longer breaks the
// enumeration mid-stream.

using System;
using System.Linq;
using CircleAI.Orchestration;
using Xunit;

namespace CircleAI.Orchestration.Tests;

public sealed class LokiSemaphoreP3Tests
{
    // Gate-off config so the dispatcher's Failed status survives without
    // being remapped to Blocked by the quality gate.
    private static readonly AgentSwarmConfig GateOffConfig = new(
        MaxConcurrency:                  4,
        TaskTimeout:                     TimeSpan.FromMinutes(5),
        RequireReviewPassBeforeDeploy:   false,
        RequireSecurityPassBeforeDeploy: false);

    [Fact]
    public async Task RunSwarmAsync_DispatcherThrows_TaskFailsButSwarmContinues()
    {
        var dispatcher = new LocalAgentDispatcher();

        // Handler #1 (Engineering) throws — used to break the whole enumeration.
        dispatcher.RegisterHandler(AgentRole.Engineering, (task, _) =>
            throw new InvalidOperationException("simulated dispatcher boom"));

        // Handler #2 (Review) returns clean.
        dispatcher.RegisterHandler(AgentRole.Review, (task, _) =>
            Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed,
                "All good.", Array.Empty<string>(),
                DateTimeOffset.UtcNow)));

        var orchestrator = new LokiOrchestrator(dispatcher, GateOffConfig);
        var tasks = new[]
        {
            AgentTask.Create(AgentRole.Engineering, "engineering work", AgentPriority.Normal),
            AgentTask.Create(AgentRole.Review,      "review work",      AgentPriority.Normal),
        };

        var results = new System.Collections.Generic.List<SwarmResult>();
        await foreach (var r in orchestrator.RunSwarmAsync(tasks)) results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Role == AgentRole.Engineering && r.Status == AgentStatus.Failed);
        Assert.Contains(results, r => r.Role == AgentRole.Review      && r.Status == AgentStatus.Passed);
    }

    [Fact]
    public async Task RunSwarmAsync_DispatcherThrows_FailureReasonCarriesException()
    {
        var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Engineering, (task, _) =>
            throw new InvalidOperationException("boom-msg-42"));

        var orchestrator = new LokiOrchestrator(dispatcher, GateOffConfig);
        var task = AgentTask.Create(AgentRole.Engineering, "work", AgentPriority.Normal);

        var results = new System.Collections.Generic.List<SwarmResult>();
        await foreach (var r in orchestrator.RunSwarmAsync(new[] { task })) results.Add(r);

        var failed = Assert.Single(results);
        Assert.Equal(AgentStatus.Failed, failed.Status);
        Assert.Contains("boom-msg-42", failed.Output);
    }
}
