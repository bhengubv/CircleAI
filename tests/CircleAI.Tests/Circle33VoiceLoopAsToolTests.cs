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
        var tool = new VoiceLoopAsTool((_, ct) => Task.Run(async () =>
        {
            await Task.Delay(5000, ct);
            return new VoiceLoopToolResult(true, "", "", TimeSpan.Zero, "", null);
        }));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

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
