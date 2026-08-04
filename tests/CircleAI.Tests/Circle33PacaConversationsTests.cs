// Circle33PacaConversationsTests.cs
//
// (3.3.0) Tests for Paca conversation runtime.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaConversationsTests
{
    private static readonly ConversationPermissions Permissive = new(AllowCloneRepos: true, AllowCreatePr: true);

    [Fact]
    public void Queue_StoresQueuedConversation()
    {
        var rt = new PacaConversationRuntime(new SuccessExecutor("ok"));
        var c = rt.Queue("c1", "p1", "agent1", "Hello");
        Assert.Equal(ConversationState.Queued, c.State);
    }

    [Fact]
    public async Task Start_TransitionsToRunningThenFinished()
    {
        var rt = new PacaConversationRuntime(new SuccessExecutor("done"));
        rt.Queue("c1", "p1", "agent1", "Hello");
        await rt.StartAsync("c1", Permissive);

        var c = rt.Get("c1");
        Assert.Equal(ConversationState.Finished, c!.State);
    }

    [Fact]
    public async Task Start_ExecutorThrows_TransitionsToFailed()
    {
        var rt = new PacaConversationRuntime(new ThrowingExecutor("boom"));
        rt.Queue("c1", "p1", "agent1", "Hello");
        await rt.StartAsync("c1", Permissive);

        var c = rt.Get("c1");
        Assert.Equal(ConversationState.Failed, c!.State);
        Assert.Equal("boom", c.FailureReason);
    }

    [Fact]
    public async Task Stop_TransitionsToStopped()
    {
        var rt = new PacaConversationRuntime(new HangingExecutor());
        rt.Queue("c1", "p1", "agent1", "Hello");
        var startTask = rt.StartAsync("c1", Permissive);

        // WAIT FOR IT TO BE RUNNING, not for 50 milliseconds. The sleep here was a
        // guess about how quickly this machine schedules a task, and under a full
        // parallel suite the guess is wrong: Stop arrives before the conversation
        // has started, so there is nothing to stop and it never reaches Stopped.
        await Eventually.TrueAsync(
            () => rt.Get("c1")?.State == ConversationState.Running,
            "the conversation to start before stopping it");

        rt.Stop("c1");

        await Eventually.CompletesAsync(startTask, "the run to unwind after Stop");
        Assert.Equal(ConversationState.Stopped, rt.Get("c1")!.State);
    }

    [Fact]
    public async Task Steps_RecordedFromExecutor()
    {
        var rt = new PacaConversationRuntime(new SteppingExecutor());
        rt.Queue("c1", "p1", "agent1", "Hello");
        await rt.StartAsync("c1", Permissive);

        var steps = rt.Steps("c1");
        Assert.Equal(2, steps.Count);
        Assert.Equal("agent", steps[0].Speaker);
    }

    [Fact]
    public async Task Start_NonQueuedConversation_Throws()
    {
        var rt = new PacaConversationRuntime(new SuccessExecutor("ok"));
        rt.Queue("c1", "p1", "agent1", "Hello");
        await rt.StartAsync("c1", Permissive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.StartAsync("c1", Permissive));
    }

    [Fact]
    public void Queue_Duplicate_Throws()
    {
        var rt = new PacaConversationRuntime(new SuccessExecutor("ok"));
        rt.Queue("c1", "p1", "agent1", "x");
        Assert.Throws<InvalidOperationException>(() => rt.Queue("c1", "p1", "agent1", "y"));
    }

    private sealed class SuccessExecutor : IConversationExecutor
    {
        private readonly string _result;
        public SuccessExecutor(string result) { _result = result; }
        public Task RunAsync(AgentConversation c, ConversationPermissions p, Action<ConversationStep> onStep, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingExecutor : IConversationExecutor
    {
        private readonly string _message;
        public ThrowingExecutor(string m) { _message = m; }
        public Task RunAsync(AgentConversation c, ConversationPermissions p, Action<ConversationStep> onStep, CancellationToken ct = default)
            => throw new InvalidOperationException(_message);
    }

    private sealed class HangingExecutor : IConversationExecutor
    {
        public Task RunAsync(AgentConversation c, ConversationPermissions p, Action<ConversationStep> onStep, CancellationToken ct = default)
            => Task.Delay(5000, ct);
    }

    private sealed class SteppingExecutor : IConversationExecutor
    {
        public Task RunAsync(AgentConversation c, ConversationPermissions p, Action<ConversationStep> onStep, CancellationToken ct = default)
        {
            onStep(new ConversationStep(c.Id, 1, "agent", """{"text":"hi"}""", DateTimeOffset.UtcNow));
            onStep(new ConversationStep(c.Id, 2, "agent", """{"text":"bye"}""", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }
}
