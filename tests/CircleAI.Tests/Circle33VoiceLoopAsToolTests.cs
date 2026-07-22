// Circle33VoiceLoopAsToolTests.cs
//
// (3.3.0) Tests for voice-loop-as-a-tool.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33VoiceLoopAsToolTests
{
    [Fact]
    public async Task Invoke_DelegatesToRunner()
    {
        var tool = new VoiceLoopAsTool((req, ct) =>
        {
            Assert.Equal("+15555550100", req.ToNumber);
            return Task.FromResult(new VoiceLoopToolResult(
                GoalAchieved: true,
                Summary:      "Booked.",
                CallId:       "c1",
                Duration:     TimeSpan.FromSeconds(45),
                Transcript:   "...",
                StructuredOutputJson: """{"appointment":"Sat 2pm"}"""));
        });

        var r = await tool.InvokeAsync(new VoiceLoopToolRequest("+15555550100", "Book haircut"));

        Assert.True(r.GoalAchieved);
        Assert.Equal("c1", r.CallId);
        Assert.Equal("Booked.", r.Summary);
    }

    [Fact]
    public async Task Invoke_MissingNumber_Throws()
    {
        var tool = new VoiceLoopAsTool((_, _) => Task.FromResult<VoiceLoopToolResult>(null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.InvokeAsync(new VoiceLoopToolRequest("", "goal")));
    }

    [Fact]
    public async Task Invoke_MissingGoal_Throws()
    {
        var tool = new VoiceLoopAsTool((_, _) => Task.FromResult<VoiceLoopToolResult>(null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.InvokeAsync(new VoiceLoopToolRequest("+15555550100", "")));
    }

    [Fact]
    public async Task Invoke_RunnerThrowsTimeout_ReturnsTimeoutResult()
    {
        var tool = new VoiceLoopAsTool((_, ct) => Task.Run(async () =>
        {
            await Task.Delay(5000, ct);
            return new VoiceLoopToolResult(true, "", "", TimeSpan.Zero, "", null);
        }));

        var r = await tool.InvokeAsync(
            new VoiceLoopToolRequest("+15555550100", "goal", MaxDuration: TimeSpan.FromMilliseconds(50)));

        Assert.False(r.GoalAchieved);
        Assert.Contains("timed out", r.Summary);
    }

    [Fact]
    public async Task Invoke_RespectsCallerCancellation()
    {
        // DETERMINISTIC: the token is already cancelled before the call, and the
        // runner blocks on a TCS the test controls — nothing depends on the
        // scheduler winning a 50 ms race.
        //
        // The old version did cts.CancelAfter(50) against a Task.Delay(5000).
        // Under load (full-suite run on a busy box) the 50 ms timer could fire
        // late enough that the assertion had already been evaluated, so this
        // failed intermittently — 7/7 alone, red inside a 13-minute run. A test
        // that fails at random trains people to ignore red, which is how real
        // regressions get waved through.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tool = new VoiceLoopAsTool(async (_, ct) =>
        {
            started.TrySetResult();
            await using (ct.Register(() => release.TrySetCanceled(ct)))
                await release.Task;               // completes only via cancellation
            return new VoiceLoopToolResult(true, "", "", TimeSpan.Zero, "", null);
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();                             // already cancelled — no timing window

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.InvokeAsync(new VoiceLoopToolRequest("+1", "goal", MaxDuration: TimeSpan.FromMinutes(5)), cts.Token));
    }

    [Fact]
    public void Descriptor_AdvertisesCorrectName()
    {
        Assert.Equal("make_voice_call", VoiceLoopAsTool.Descriptor.Name);
        Assert.Contains("phone call", VoiceLoopAsTool.Descriptor.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_NullRunner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new VoiceLoopAsTool(null!));
    }
}
