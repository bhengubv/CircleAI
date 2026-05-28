using CircleAI.Orchestration;
using Xunit;

namespace CircleAI.Orchestration.Tests;

public sealed class LokiOrchestratorTests
{
    // -----------------------------------------------------------------------
    // 1. Single passing task → AgentStatus.Passed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunSwarmAsync_SinglePassingTask_YieldsPassed()
    {
        var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Engineering, (task, _) =>
            Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed,
                "All good.", Array.Empty<string>(),
                DateTimeOffset.UtcNow)));

        var orchestrator = new LokiOrchestrator(dispatcher);
        var task = AgentTask.Create(AgentRole.Engineering, "build feature", AgentPriority.Normal);

        var results = await CollectAsync(orchestrator.RunSwarmAsync(new[] { task }));

        Assert.Single(results);
        Assert.Equal(AgentStatus.Passed, results[0].Status);
    }

    // -----------------------------------------------------------------------
    // 2. [CRITICAL] issue → quality gate blocks (AgentStatus.Blocked)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunSwarmAsync_CriticalIssue_YieldsBlocked()
    {
        var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Review, (task, _) =>
            Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed,
                "Review output.",
                new[] { "[CRITICAL] Security vulnerability detected." },
                DateTimeOffset.UtcNow)));

        var orchestrator = new LokiOrchestrator(dispatcher,
            new AgentSwarmConfig(1, TimeSpan.FromMinutes(5), true, true));
        var task = AgentTask.Create(AgentRole.Review, "review code", AgentPriority.Normal);

        var results = await CollectAsync(orchestrator.RunSwarmAsync(new[] { task }));

        Assert.Single(results);
        Assert.Equal(AgentStatus.Blocked, results[0].Status);
    }

    // -----------------------------------------------------------------------
    // 3. [LOW] issue → gate passes, issue in Warnings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunSwarmAsync_LowSeverityIssue_PassesGate()
    {
        var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Engineering, (task, _) =>
            Task.FromResult(new SwarmResult(
                task.Id, task.Role, AgentStatus.Passed,
                "Done.",
                new[] { "[LOW] Minor style nit." },
                DateTimeOffset.UtcNow)));

        var orchestrator = new LokiOrchestrator(dispatcher);
        var task = AgentTask.Create(AgentRole.Engineering, "lint", AgentPriority.Low);

        var results = await CollectAsync(orchestrator.RunSwarmAsync(new[] { task }));

        Assert.Single(results);
        // Status must NOT be blocked — the low issue is a warning only.
        Assert.NotEqual(AgentStatus.Blocked, results[0].Status);

        // Confirm the quality gate sees it as a warning.
        var gate = await dispatcher.RunQualityGateAsync(results[0]);
        Assert.True(gate.Passed);
        Assert.Empty(gate.Blockers);
        Assert.Single(gate.Warnings);
    }

    // -----------------------------------------------------------------------
    // 4. Timeout → AgentStatus.Failed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunSwarmAsync_TaskExceedsTimeout_YieldsFailed()
    {
        var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Operations, async (task, ct) =>
        {
            // Simulate infinite work; the orchestrator's timeout will cancel this.
            await Task.Delay(Timeout.Infinite, ct);
            return new SwarmResult(task.Id, task.Role, AgentStatus.Passed,
                "Never reached.", Array.Empty<string>(), DateTimeOffset.UtcNow);
        });

        // Very short timeout to keep the test fast.
        var config = new AgentSwarmConfig(1, TimeSpan.FromMilliseconds(50), false, false);
        var orchestrator = new LokiOrchestrator(dispatcher, config);
        var task = AgentTask.Create(AgentRole.Operations, "long op", AgentPriority.Normal);

        var results = await CollectAsync(orchestrator.RunSwarmAsync(new[] { task }));

        Assert.Single(results);
        Assert.Equal(AgentStatus.Failed, results[0].Status);
        Assert.Contains(results[0].Issues,
            i => i.StartsWith("[HIGH]", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // 5. No handler registered → AgentStatus.Blocked with descriptive message
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunSwarmAsync_NoHandlerRegistered_YieldsBlockedWithMessage()
    {
        var dispatcher = new LocalAgentDispatcher(); // no handler for Security
        // Disable gate enforcement so the blocked result is yielded as-is.
        var config = new AgentSwarmConfig(1, TimeSpan.FromMinutes(1), false, false);
        var orchestrator = new LokiOrchestrator(dispatcher, config);
        var task = AgentTask.Create(AgentRole.Security, "pen test", AgentPriority.Critical);

        var results = await CollectAsync(orchestrator.RunSwarmAsync(new[] { task }));

        Assert.Single(results);
        Assert.Equal(AgentStatus.Blocked, results[0].Status);
        Assert.NotEmpty(results[0].Output);
        Assert.NotEmpty(results[0].Issues);
    }

    // -----------------------------------------------------------------------
    // 6. MaxConcurrency=1 → tasks execute sequentially
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunSwarmAsync_MaxConcurrencyOne_ExecutesSequentially()
    {
        int activeCount = 0;
        int maxObserved = 0;
        var gate = new SemaphoreSlim(0, 1); // used to serialise observation

        var dispatcher = new LocalAgentDispatcher();
        dispatcher.RegisterHandler(AgentRole.Engineering, async (task, ct) =>
        {
            var current = Interlocked.Increment(ref activeCount);

            // Atomically capture the maximum concurrent count.
            int observed;
            do
            {
                observed = maxObserved;
                if (current <= observed) break;
            } while (Interlocked.CompareExchange(ref maxObserved, current, observed) != observed);

            await Task.Delay(10, ct); // brief pause to allow overlap if concurrency > 1

            Interlocked.Decrement(ref activeCount);

            return new SwarmResult(task.Id, task.Role, AgentStatus.Passed,
                "done", Array.Empty<string>(), DateTimeOffset.UtcNow);
        });

        var config = new AgentSwarmConfig(
            MaxConcurrency: 1,
            TaskTimeout: TimeSpan.FromSeconds(30),
            RequireReviewPassBeforeDeploy: false,
            RequireSecurityPassBeforeDeploy: false);

        var orchestrator = new LokiOrchestrator(dispatcher, config);

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => AgentTask.Create(AgentRole.Engineering, "work", AgentPriority.Normal));

        var results = await CollectAsync(orchestrator.RunSwarmAsync(tasks));

        Assert.Equal(4, results.Count);
        // With MaxConcurrency=1 the semaphore ensures at most 1 task runs at a time.
        Assert.Equal(1, maxObserved);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<List<SwarmResult>> CollectAsync(
        IAsyncEnumerable<SwarmResult> source)
    {
        var list = new List<SwarmResult>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
