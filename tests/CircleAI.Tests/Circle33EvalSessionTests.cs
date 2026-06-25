// Circle33EvalSessionTests.cs
//
// (3.3.0) Tests for EvalSession harness.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33EvalSessionTests
{
    [Fact]
    public async Task RunAsync_RunsEveryTurnInScript()
    {
        var calls = 0;
        var session = new EvalSession((transcript, ct) =>
        {
            calls++;
            return Task.FromResult($"Got: {transcript}");
        });

        var result = await session.RunAsync(new[]
        {
            new EvalTurn("hello"),
            new EvalTurn("hi"),
            new EvalTurn("bye"),
        });

        Assert.Equal(3, calls);
        Assert.Equal(3, result.Turns.Count);
    }

    [Fact]
    public async Task RunAsync_AllKeywordsPresent_AllHitTrue()
    {
        var session = new EvalSession((_, _) => Task.FromResult("Your refund is 30 days."));

        var result = await session.RunAsync(new[]
        {
            new EvalTurn("how long is the refund window?",
                ExpectedKeywords: new[] { "refund", "30 days" }),
        });

        Assert.True(result.AllKeywordsHit);
        Assert.Empty(result.Turns[0].MissingKeywords);
    }

    [Fact]
    public async Task RunAsync_MissingKeyword_RecordsIt()
    {
        var session = new EvalSession((_, _) => Task.FromResult("I can help."));

        var result = await session.RunAsync(new[]
        {
            new EvalTurn("refund?",
                ExpectedKeywords: new[] { "refund", "30 days" }),
        });

        Assert.False(result.AllKeywordsHit);
        Assert.Equal(2, result.Turns[0].MissingKeywords.Count);
        Assert.Contains("refund",  result.Turns[0].MissingKeywords);
        Assert.Contains("30 days", result.Turns[0].MissingKeywords);
    }

    [Fact]
    public async Task RunAsync_KeywordCaseInsensitive()
    {
        var session = new EvalSession((_, _) => Task.FromResult("REFUND POLICY"));

        var result = await session.RunAsync(new[]
        {
            new EvalTurn("refund?",
                ExpectedKeywords: new[] { "refund" }),
        });

        Assert.True(result.AllKeywordsHit);
    }

    [Fact]
    public async Task RunAsync_TotalLatency_SumsTurnLatencies()
    {
        var session = new EvalSession(async (_, _) =>
        {
            await Task.Delay(20);
            return "ok";
        });

        var result = await session.RunAsync(new[]
        {
            new EvalTurn("hi"),
            new EvalTurn("hi"),
            new EvalTurn("hi"),
        });

        Assert.True(result.TotalLatency.TotalMilliseconds >= 60);
    }

    [Fact]
    public async Task RunAsync_EmptyScript_ReturnsEmpty()
    {
        var session = new EvalSession((_, _) => Task.FromResult("never called"));
        var result = await session.RunAsync(Array.Empty<EvalTurn>());
        Assert.Empty(result.Turns);
        Assert.True(result.AllKeywordsHit);
    }

    [Fact]
    public void Constructor_NullHandler_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EvalSession(null!));
    }
}
