// Circle33TelemetryTests.cs
//
// (3.3.0) Tests for OpenTelemetry-style spans via ActivitySource.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33TelemetryTests
{
    [Fact]
    public void StartTurn_CreatesActivityWithCallIdTag()
    {
        var listener = StartListening();
        using var activity = VoiceLoopTelemetry.StartTurn("call-1");
        Assert.NotNull(activity);
        Assert.Equal("voice_loop.turn", activity!.OperationName);
        Assert.Equal("call-1", activity.GetTagItem("call.id"));
        listener.Dispose();
    }

    [Fact]
    public void StartAsr_TagsBackend()
    {
        var listener = StartListening();
        using var activity = VoiceLoopTelemetry.StartAsr("whisper");
        Assert.NotNull(activity);
        Assert.Equal("voice_loop.asr", activity!.OperationName);
        Assert.Equal("whisper", activity.GetTagItem("backend"));
        listener.Dispose();
    }

    [Fact]
    public void StartLlm_TagsProviderAndModel()
    {
        var listener = StartListening();
        using var activity = VoiceLoopTelemetry.StartLlm("openai", "gpt-4o-mini");
        Assert.NotNull(activity);
        Assert.Equal("openai", activity!.GetTagItem("provider"));
        Assert.Equal("gpt-4o-mini", activity.GetTagItem("model"));
        listener.Dispose();
    }

    [Fact]
    public void StartTts_TagsBackendAndVoice()
    {
        var listener = StartListening();
        using var activity = VoiceLoopTelemetry.StartTts("elevenlabs", "Rachel");
        Assert.NotNull(activity);
        Assert.Equal("elevenlabs", activity!.GetTagItem("backend"));
        Assert.Equal("Rachel", activity.GetTagItem("voice"));
        listener.Dispose();
    }

    [Fact]
    public void RecordOutcome_Success_SetsOkStatus()
    {
        var listener = StartListening();
        using var activity = VoiceLoopTelemetry.StartTurn("call-2");
        VoiceLoopTelemetry.RecordOutcome(activity, success: true);
        Assert.Equal(ActivityStatusCode.Ok, activity!.Status);
        Assert.Equal("success", activity.GetTagItem("outcome"));
        listener.Dispose();
    }

    [Fact]
    public void RecordOutcome_Failure_SetsErrorStatusAndMessage()
    {
        var listener = StartListening();
        using var activity = VoiceLoopTelemetry.StartTurn("call-3");
        VoiceLoopTelemetry.RecordOutcome(activity, success: false, errorReason: "tts failed");
        Assert.Equal(ActivityStatusCode.Error, activity!.Status);
        Assert.Equal("failure", activity.GetTagItem("outcome"));
        Assert.Equal("tts failed", activity.GetTagItem("error.message"));
        listener.Dispose();
    }

    [Fact]
    public void RecordOutcome_NullActivity_DoesNotThrow()
    {
        VoiceLoopTelemetry.RecordOutcome(null, success: true);
    }

    [Fact]
    public void SourceName_StableForDashboards()
    {
        Assert.Equal("CircleAI.Telephony.VoiceLoop", VoiceLoopTelemetry.SourceName);
    }

    private static ActivityListener StartListening()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo      = source => source.Name == VoiceLoopTelemetry.SourceName,
            Sample              = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string>           _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
