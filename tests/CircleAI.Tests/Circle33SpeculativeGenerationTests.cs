// Circle33SpeculativeGenerationTests.cs
//
// (3.3.0) Tests for speculative generation.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33SpeculativeGenerationTests
{
    [Fact]
    public void Speculate_BelowMinLength_NoBranch()
    {
        var g = new DefaultSpeculativeGenerator(minPartialLength: 10);
        g.Speculate("hi", (_, _) => Task.FromResult("draft"));
        Assert.Null(g.ActiveBranch);
    }

    [Fact]
    public void Speculate_SetsActiveBranch()
    {
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello there", (_, _) => Task.FromResult("draft"));
        Assert.NotNull(g.ActiveBranch);
        Assert.Equal("hello there", g.ActiveBranch!.PartialTranscript);
    }

    [Fact]
    public void Speculate_ExtensionOfActive_KeepsExisting()
    {
        int calls = 0;
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", (_, _) => { calls++; return Task.FromResult("draft"); });
        g.Speculate("hello world", (_, _) => { calls++; return Task.FromResult("draft2"); });
        Assert.Equal(1, calls);
        Assert.Equal("hello", g.ActiveBranch!.PartialTranscript);
    }

    [Fact]
    public void Speculate_DivergentPartial_StartsNewBranch()
    {
        int calls = 0;
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", (_, _) => { calls++; return Task.FromResult("draft"); });
        g.Speculate("goodbye", (_, _) => { calls++; return Task.FromResult("draft2"); });
        Assert.Equal(2, calls);
        Assert.Equal("goodbye", g.ActiveBranch!.PartialTranscript);
    }

    [Fact]
    public async Task CommitAsync_FinalEqualsPartial_ReturnsDraft()
    {
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", (_, _) => Task.FromResult("draft-response"));

        var result = await g.CommitAsync("hello", (_, _) => Task.FromResult("fresh-response"));
        Assert.Equal("draft-response", result);
    }

    [Fact]
    public async Task CommitAsync_FinalDiverges_GeneratesFresh()
    {
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", (_, _) => Task.FromResult("draft-response"));

        var result = await g.CommitAsync("goodbye", (_, _) => Task.FromResult("fresh-response"));
        Assert.Equal("fresh-response", result);
    }

    [Fact]
    public async Task CommitAsync_FinalExtends_RegeneratesWithFullTranscript()
    {
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", (_, _) => Task.FromResult("partial-draft"));

        var result = await g.CommitAsync("hello world", (transcript, _) => Task.FromResult($"final-{transcript}"));
        Assert.Equal("final-hello world", result);
    }

    [Fact]
    public async Task CommitAsync_NoActiveBranch_GeneratesFresh()
    {
        var g = new DefaultSpeculativeGenerator();
        var result = await g.CommitAsync("hello", (_, _) => Task.FromResult("fresh"));
        Assert.Equal("fresh", result);
    }

    [Fact]
    public void Abort_ClearsActiveBranch()
    {
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", (_, _) => Task.FromResult("draft"));
        Assert.NotNull(g.ActiveBranch);
        g.Abort();
        Assert.Null(g.ActiveBranch);
    }

    [Fact]
    public async Task Speculate_Divergent_CancelsPreviousGenerator()
    {
        bool firstCancelled = false;
        // The first draft must be IN FLIGHT before the second supersedes it —
        // otherwise there is nothing to cancel and the assertion fails for reasons
        // unrelated to superseding. Signal it instead of sleeping on it.
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var g = new DefaultSpeculativeGenerator(minPartialLength: 3);
        g.Speculate("hello", async (t, ct) =>
        {
            firstStarted.TrySetResult();
            try { await Task.Delay(5000, ct); return "draft1"; }
            catch (OperationCanceledException) { Volatile.Write(ref firstCancelled, true); throw; }
        });

        await Eventually.CompletesAsync(firstStarted.Task, "the first draft to start");
        g.Speculate("goodbye", (_, _) => Task.FromResult("draft2"));
        await Eventually.TrueAsync(() => Volatile.Read(ref firstCancelled),
            "the superseded first draft to be cancelled");

        Assert.True(firstCancelled);
    }
}
