// Circle33CircuitBreakerTests.cs
//
// (3.3.0) Tests for the per-tool circuit breaker + timeout wrapper.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33CircuitBreakerTests
{
    private static readonly ToolDefinition AnyTool = new("any", "any tool", "{}");

    [Fact]
    public async Task SuccessfulInvocation_KeepsBreakerClosed()
    {
        var inner = new SuccessRegistry();
        var cb = new CircuitBreakerToolRegistry(inner);
        cb.RegisterLocal(AnyTool, (_, _) => ValueTask.FromResult("{}"));

        var r = await cb.InvokeAsync(new ToolInvocation("c1", "any", "{}"));

        Assert.True(r.Succeeded);
        Assert.Equal(ToolBreakerState.Closed, cb.GetState("any"));
    }

    [Fact]
    public async Task ConsecutiveFailures_TripBreaker()
    {
        var inner = new FailingRegistry();
        var cb = new CircuitBreakerToolRegistry(inner,
            defaultPolicy: new ToolCallPolicy(FailureThreshold: 3));
        cb.RegisterLocal(AnyTool, (_, _) => throw new InvalidOperationException("boom"));

        for (int i = 0; i < 3; i++)
        {
            var r = await cb.InvokeAsync(new ToolInvocation($"c{i}", "any", "{}"));
            Assert.False(r.Succeeded);
        }

        Assert.Equal(ToolBreakerState.Open, cb.GetState("any"));

        // Next call should short-circuit without touching the inner registry.
        var blocked = await cb.InvokeAsync(new ToolInvocation("c-blocked", "any", "{}"));
        Assert.False(blocked.Succeeded);
        Assert.Contains("circuit-broken", blocked.Error);
        Assert.Equal(3, inner.CallCount); // not incremented past 3
    }

    [Fact]
    public async Task OpenBreaker_TransitionsToHalfOpenAfterDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var inner = new FailingRegistry();
        var cb = new CircuitBreakerToolRegistry(
            inner,
            defaultPolicy: new ToolCallPolicy(FailureThreshold: 2, OpenDuration: TimeSpan.FromSeconds(5)),
            clock: () => now);
        cb.RegisterLocal(AnyTool, (_, _) => throw new InvalidOperationException("boom"));

        await cb.InvokeAsync(new ToolInvocation("c1", "any", "{}"));
        await cb.InvokeAsync(new ToolInvocation("c2", "any", "{}"));
        Assert.Equal(ToolBreakerState.Open, cb.GetState("any"));

        now = now + TimeSpan.FromSeconds(6);
        Assert.Equal(ToolBreakerState.HalfOpen, cb.GetState("any"));
    }

    [Fact]
    public async Task Timeout_FailsButDoesNotPropagateCancellation()
    {
        var inner = new DefaultToolCallRegistry(new System.Net.Http.HttpClient());
        var cb = new CircuitBreakerToolRegistry(
            inner,
            defaultPolicy: new ToolCallPolicy(Timeout: TimeSpan.FromMilliseconds(50)));
        // Never respond, rather than respond in 500 ms against a 50 ms timeout. A
        // 10x margin is more comfortable than the 4x that actually flaked elsewhere,
        // but it is the same bet on two timer callbacks landing in order under load.
        // The timeout cancels ct, so this returns promptly either way.
        cb.RegisterLocal(AnyTool, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "{}";
        });

        var r = await cb.InvokeAsync(new ToolInvocation("c1", "any", "{}"));
        Assert.False(r.Succeeded);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task PerToolPolicy_Overrides_Default()
    {
        var inner = new FailingRegistry();
        var cb = new CircuitBreakerToolRegistry(
            inner,
            defaultPolicy: new ToolCallPolicy(FailureThreshold: 10));
        cb.RegisterLocal(AnyTool, (_, _) => throw new InvalidOperationException("boom"));
        cb.SetPolicy("any", new ToolCallPolicy(FailureThreshold: 2));

        await cb.InvokeAsync(new ToolInvocation("c1", "any", "{}"));
        await cb.InvokeAsync(new ToolInvocation("c2", "any", "{}"));
        Assert.Equal(ToolBreakerState.Open, cb.GetState("any"));
    }

    [Fact]
    public async Task SuccessAfterFailures_ResetsCounter()
    {
        var inner = new ToggleRegistry();
        var cb = new CircuitBreakerToolRegistry(inner,
            defaultPolicy: new ToolCallPolicy(FailureThreshold: 3));
        cb.RegisterLocal(AnyTool, (_, _) => ValueTask.FromResult("{}"));

        await cb.InvokeAsync(new ToolInvocation("c1", "any", "{}")); // fail
        await cb.InvokeAsync(new ToolInvocation("c2", "any", "{}")); // fail
        await cb.InvokeAsync(new ToolInvocation("c3", "any", "{}")); // success → reset
        await cb.InvokeAsync(new ToolInvocation("c4", "any", "{}")); // fail
        await cb.InvokeAsync(new ToolInvocation("c5", "any", "{}")); // fail

        // 2 fails after the success → still under threshold.
        Assert.Equal(ToolBreakerState.Closed, cb.GetState("any"));
    }

    [Fact]
    public void SetPolicy_NullPolicy_Throws()
    {
        var cb = new CircuitBreakerToolRegistry(new SuccessRegistry());
        Assert.Throws<ArgumentNullException>(() => cb.SetPolicy("any", null!));
    }

    [Fact]
    public async Task UnknownTool_DoesNotCrashBreaker()
    {
        var cb = new CircuitBreakerToolRegistry(new EmptyRegistry());
        var r = await cb.InvokeAsync(new ToolInvocation("c1", "ghost", "{}"));
        Assert.False(r.Succeeded);
    }

    // ===== Helpers =====

    private sealed class SuccessRegistry : IToolCallRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => new[] { AnyTool };
        public void RegisterLocal(ToolDefinition d, LocalToolHandler h) { }
        public void RegisterWebhook(ToolDefinition d, Uri u) { }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
            => ValueTask.FromResult(new ToolResult(invocation.CallId, true, "{}"));
    }

    private sealed class FailingRegistry : IToolCallRegistry
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<ToolDefinition> Definitions => new[] { AnyTool };
        public void RegisterLocal(ToolDefinition d, LocalToolHandler h) { }
        public void RegisterWebhook(ToolDefinition d, Uri u) { }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            CallCount++;
            return ValueTask.FromResult(new ToolResult(invocation.CallId, false, "{}", "underlying error"));
        }
    }

    private sealed class ToggleRegistry : IToolCallRegistry
    {
        private int _calls;
        public IReadOnlyList<ToolDefinition> Definitions => new[] { AnyTool };
        public void RegisterLocal(ToolDefinition d, LocalToolHandler h) { }
        public void RegisterWebhook(ToolDefinition d, Uri u) { }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            _calls++;
            // Pattern: fail, fail, succeed, fail, fail, ...
            bool ok = _calls == 3;
            return ValueTask.FromResult(new ToolResult(invocation.CallId, ok, "{}", ok ? null : "err"));
        }
    }

    private sealed class EmptyRegistry : IToolCallRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => Array.Empty<ToolDefinition>();
        public void RegisterLocal(ToolDefinition d, LocalToolHandler h) { }
        public void RegisterWebhook(ToolDefinition d, Uri u) { }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
            => ValueTask.FromResult(new ToolResult(invocation.CallId, false, "{}", "not registered"));
    }
}
