// Circle33LlmJudgeTests.cs
//
// (3.3.0) Tests for LlmJudge.

using System;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33LlmJudgeTests
{
    private static readonly JudgeDimension Accuracy = new("accuracy", "Is the answer factually correct?");
    private static readonly JudgeDimension Tone     = new("tone",     "Is the tone warm and professional?");

    [Fact]
    public async Task Judge_HappyPath_ParsesScoresAndOverall()
    {
        var judge = new LlmJudge((prompt, ct) => Task.FromResult("""
        { "scores": { "accuracy": 8, "tone": 9 }, "overall": "pass", "reasoning": "Looks good." }
        """));

        var v = await judge.JudgeAsync("test?", "test answer", new[] { Accuracy, Tone });

        Assert.Equal(8, v.Scores["accuracy"]);
        Assert.Equal(9, v.Scores["tone"]);
        Assert.Equal("pass", v.Overall);
        Assert.Contains("Looks good", v.Reasoning);
    }

    [Fact]
    public async Task Judge_PromptIncludesDimensions()
    {
        string captured = "";
        var judge = new LlmJudge((prompt, ct) =>
        {
            captured = prompt;
            return Task.FromResult("""{ "scores": { "accuracy": 5, "tone": 5 }, "overall": "borderline", "reasoning": "" }""");
        });

        await judge.JudgeAsync("u", "r", new[] { Accuracy, Tone });

        Assert.Contains("accuracy", captured);
        Assert.Contains("tone", captured);
    }

    [Fact]
    public async Task Judge_TextWrappedInProse_StillParses()
    {
        var judge = new LlmJudge((_, _) => Task.FromResult("""
        Sure! Here's my JSON:
        { "scores": { "accuracy": 7, "tone": 6 }, "overall": "pass", "reasoning": "ok" }
        Let me know if you'd like more.
        """));

        var v = await judge.JudgeAsync("u", "r", new[] { Accuracy, Tone });
        Assert.Equal(7, v.Scores["accuracy"]);
        Assert.Equal("pass", v.Overall);
    }

    [Fact]
    public async Task Judge_UnparseableReply_ReturnsBorderlineAndZeros()
    {
        var judge = new LlmJudge((_, _) => Task.FromResult("blah blah no json"));

        var v = await judge.JudgeAsync("u", "r", new[] { Accuracy });

        Assert.Equal(0, v.Scores["accuracy"]);
        Assert.Equal("borderline", v.Overall);
    }

    [Fact]
    public async Task Judge_MissingDimensionScore_DefaultsToZero()
    {
        var judge = new LlmJudge((_, _) => Task.FromResult("""
        { "scores": { "accuracy": 8 }, "overall": "pass", "reasoning": "" }
        """));

        var v = await judge.JudgeAsync("u", "r", new[] { Accuracy, Tone });

        Assert.Equal(8, v.Scores["accuracy"]);
        Assert.Equal(0, v.Scores["tone"]);
    }

    [Fact]
    public async Task Judge_ScoreAsString_StillParses()
    {
        var judge = new LlmJudge((_, _) => Task.FromResult("""
        { "scores": { "accuracy": "7" }, "overall": "pass", "reasoning": "" }
        """));

        var v = await judge.JudgeAsync("u", "r", new[] { Accuracy });
        Assert.Equal(7, v.Scores["accuracy"]);
    }

    [Fact]
    public void Constructor_NullCompletion_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LlmJudge(null!));
    }
}
