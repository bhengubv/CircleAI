// Circle33EndOfTurnTests.cs
//
// (3.3.0) Tests for end-of-turn detectors.

using System;
using CircleAI.Speech;
using Xunit;

namespace CircleAI.Tests;

public class Circle33EndOfTurnTests
{
    [Fact]
    public void Null_AlwaysComplete()
    {
        var d = NullEndOfTurnDetector.Instance;
        var r = d.Predict("anything", TimeSpan.Zero);
        Assert.True(r.IsComplete);
        Assert.Equal("null", d.BackendId);
    }

    [Fact]
    public void Rules_TerminalPunctuationAndMinSilence_Completes()
    {
        var d = new RuleBasedEndOfTurnDetector(minSilence: TimeSpan.FromMilliseconds(200));
        var r = d.Predict("hello.", TimeSpan.FromMilliseconds(300));
        Assert.True(r.IsComplete);
        Assert.True(r.Confidence > 0.7f);
    }

    [Fact]
    public void Rules_HangingWord_WaitsLonger()
    {
        var d = new RuleBasedEndOfTurnDetector(
            minSilence:     TimeSpan.FromMilliseconds(200),
            hangingSilence: TimeSpan.FromMilliseconds(900));
        var r = d.Predict("I was going to and", TimeSpan.FromMilliseconds(300));
        Assert.False(r.IsComplete);
        Assert.True(r.WaitMoreMs > 0);
    }

    [Fact]
    public void Rules_MaxSilence_Overrides()
    {
        var d = new RuleBasedEndOfTurnDetector(
            maxSilence: TimeSpan.FromMilliseconds(1000));
        var r = d.Predict("I was going to and", TimeSpan.FromMilliseconds(1200));
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void Rules_EmptyTranscript_NotComplete()
    {
        var d = new RuleBasedEndOfTurnDetector();
        var r = d.Predict("", TimeSpan.FromMilliseconds(50));
        Assert.False(r.IsComplete);
        Assert.True(r.WaitMoreMs >= 150);
    }

    [Fact]
    public void Rules_NoPunctuationButLongSilence_Completes()
    {
        var d = new RuleBasedEndOfTurnDetector(minSilence: TimeSpan.FromMilliseconds(300));
        var r = d.Predict("hello there", TimeSpan.FromMilliseconds(400));
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void Rules_ShortSilence_NotComplete()
    {
        var d = new RuleBasedEndOfTurnDetector(minSilence: TimeSpan.FromMilliseconds(500));
        var r = d.Predict("hello.", TimeSpan.FromMilliseconds(100));
        Assert.False(r.IsComplete);
        Assert.True(r.WaitMoreMs > 0);
    }

    [Fact]
    public void Rules_HangingWordPastHangingSilence_Completes()
    {
        var d = new RuleBasedEndOfTurnDetector(
            hangingSilence: TimeSpan.FromMilliseconds(500));
        var r = d.Predict("and", TimeSpan.FromMilliseconds(600));
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void SmartTurn_NoRunner_FallsBackToRules()
    {
        var d = new SmartTurnDetector();
        Assert.Equal("smart-turn (fallback)", d.BackendId);
        var r = d.Predict("hello.", TimeSpan.FromMilliseconds(500));
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void SmartTurn_WithRunnerAboveThreshold_Completes()
    {
        var d = new SmartTurnDetector(new FixedRunner(0.9f), threshold: 0.5f);
        Assert.Equal("smart-turn-v2", d.BackendId);
        var r = d.Predict("hello", TimeSpan.FromMilliseconds(100));
        Assert.True(r.IsComplete);
        Assert.Equal(0.9f, r.Confidence, 2);
    }

    [Fact]
    public void SmartTurn_WithRunnerBelowThreshold_WaitsMore()
    {
        var d = new SmartTurnDetector(new FixedRunner(0.2f), threshold: 0.5f);
        var r = d.Predict("hello", TimeSpan.FromMilliseconds(100));
        Assert.False(r.IsComplete);
        Assert.True(r.WaitMoreMs > 0);
    }

    [Fact]
    public void SmartTurn_RunnerOutOfRange_Clamped()
    {
        var d = new SmartTurnDetector(new FixedRunner(2.0f), threshold: 0.5f);
        var r = d.Predict("hello", TimeSpan.Zero);
        Assert.True(r.IsComplete);
        Assert.True(r.Confidence <= 1.0f);
    }

    private sealed class FixedRunner : ITurnModelRunner
    {
        private readonly float _v;
        public FixedRunner(float v) { _v = v; }
        public float ScoreCompletion(string partialTranscript, TimeSpan trailingSilence) => _v;
    }
}
