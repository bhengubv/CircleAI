// Circle33BargeInTests.cs
//
// (3.3.0) Tests for barge-in controller.

using System;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33BargeInTests
{
    [Fact]
    public void OnPlaybackStart_SetsSpeaking()
    {
        var c = new BargeInController();
        c.OnPlaybackStart();
        Assert.Equal(BargeInState.Speaking, c.State);
        Assert.True(c.ShouldEmitAudio);
    }

    [Fact]
    public void ShortSpeechBlip_DoesNotPause()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BargeInController(
            new BargeInOptions(PauseAfter: TimeSpan.FromMilliseconds(100)),
            clock: () => now);
        c.OnPlaybackStart();

        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(50);
        var transition = c.OnCallerSpeech();

        Assert.Null(transition);
        Assert.Equal(BargeInState.Speaking, c.State);
    }

    [Fact]
    public void SustainedSpeech_TransitionsToPaused()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BargeInController(
            new BargeInOptions(PauseAfter: TimeSpan.FromMilliseconds(100)),
            clock: () => now);
        c.OnPlaybackStart();

        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(150);
        var transition = c.OnCallerSpeech();

        Assert.NotNull(transition);
        Assert.Equal(BargeInState.Speaking, transition!.From);
        Assert.Equal(BargeInState.Paused,   transition.To);
        Assert.Equal(BargeInState.Paused,   c.State);
        Assert.False(c.ShouldEmitAudio);
    }

    [Fact]
    public void SilenceAfterPause_Resumes()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BargeInController(
            new BargeInOptions(PauseAfter: TimeSpan.FromMilliseconds(100)),
            clock: () => now);
        c.OnPlaybackStart();

        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(150);
        c.OnCallerSpeech();
        Assert.Equal(BargeInState.Paused, c.State);

        now = now + TimeSpan.FromMilliseconds(50);
        var resume = c.OnCallerSilence();

        Assert.NotNull(resume);
        Assert.Equal(BargeInState.Resumed, resume!.To);
        Assert.Equal(BargeInState.Speaking, c.State);
        Assert.True(c.ShouldEmitAudio);
    }

    [Fact]
    public void ContinuedSpeechPastCancelThreshold_TransitionsToCancelled()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BargeInController(
            new BargeInOptions(PauseAfter: TimeSpan.FromMilliseconds(100), CancelAfter: TimeSpan.FromMilliseconds(500)),
            clock: () => now);
        c.OnPlaybackStart();

        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(150);
        c.OnCallerSpeech();
        Assert.Equal(BargeInState.Paused, c.State);

        now = now + TimeSpan.FromMilliseconds(400);
        var cancel = c.OnCallerSpeech();

        Assert.NotNull(cancel);
        Assert.Equal(BargeInState.Cancelled, cancel!.To);
        Assert.True(c.WasBargedIn);
        Assert.False(c.ShouldEmitAudio);
    }

    [Fact]
    public void Cancelled_State_DoesNotTransition()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BargeInController(
            new BargeInOptions(PauseAfter: TimeSpan.FromMilliseconds(50), CancelAfter: TimeSpan.FromMilliseconds(100)),
            clock: () => now);
        c.OnPlaybackStart();

        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(60);
        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(60);
        c.OnCallerSpeech();
        Assert.Equal(BargeInState.Cancelled, c.State);

        // Subsequent events should be ignored.
        Assert.Null(c.OnCallerSpeech());
        Assert.Null(c.OnCallerSilence());
    }

    [Fact]
    public void Silence_BeforePause_ResetsSpeechTimer()
    {
        var now = DateTimeOffset.UtcNow;
        var c = new BargeInController(
            new BargeInOptions(PauseAfter: TimeSpan.FromMilliseconds(100)),
            clock: () => now);
        c.OnPlaybackStart();

        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(50);
        c.OnCallerSilence();

        // New speech burst starts the timer fresh.
        c.OnCallerSpeech();
        now = now + TimeSpan.FromMilliseconds(80);
        Assert.Null(c.OnCallerSpeech());
        Assert.Equal(BargeInState.Speaking, c.State);
    }
}
