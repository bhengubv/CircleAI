// Circle33StreamingToolTests.cs
//
// (3.3.0) Tests for streaming tool progress updates.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33StreamingToolTests
{
    [Fact]
    public async Task RecordingSink_CapturesEveryUpdate()
    {
        var sink = new RecordingToolProgressSink();
        var invocation = new ToolInvocation("c1", "long_lookup", "{}");

        StreamingToolHandler handler = async (args, s, ct) =>
        {
            await s.EmitAsync(new ToolProgressUpdate("c1", 25, "step 1", DateTimeOffset.UtcNow), ct);
            await s.EmitAsync(new ToolProgressUpdate("c1", 50, "step 2", DateTimeOffset.UtcNow), ct);
            await s.EmitAsync(new ToolProgressUpdate("c1", 100, "done", DateTimeOffset.UtcNow), ct);
            return """{"ok":true}""";
        };

        var result = await StreamingToolRunner.RunAsync(invocation, handler, sink);

        Assert.True(result.Succeeded);
        Assert.Equal(3, sink.Updates.Count);
        Assert.Equal(25,  sink.Updates[0].PercentComplete);
        Assert.Equal(100, sink.Updates[2].PercentComplete);
    }

    [Fact]
    public async Task SpokenSink_Throttles_BelowInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new FakeCallSession();
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        var sink = new SpokenToolProgressSink(session, tts, TimeSpan.FromSeconds(2), clock: () => now);

        await sink.EmitAsync(new ToolProgressUpdate("c1", 25, "step 1", now));
        now = now + TimeSpan.FromMilliseconds(500);
        await sink.EmitAsync(new ToolProgressUpdate("c1", 50, "step 2", now));
        now = now + TimeSpan.FromMilliseconds(500);
        await sink.EmitAsync(new ToolProgressUpdate("c1", 75, "step 3", now));

        Assert.Single(spoken);
        Assert.Equal("step 1", spoken[0]);
    }

    [Fact]
    public async Task SpokenSink_SpeaksAfterIntervalElapses()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new FakeCallSession();
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        var sink = new SpokenToolProgressSink(session, tts, TimeSpan.FromMilliseconds(100), clock: () => now);

        await sink.EmitAsync(new ToolProgressUpdate("c1", 25, "step 1", now));
        now = now + TimeSpan.FromMilliseconds(200);
        await sink.EmitAsync(new ToolProgressUpdate("c1", 50, "step 2", now));
        now = now + TimeSpan.FromMilliseconds(200);
        await sink.EmitAsync(new ToolProgressUpdate("c1", 75, "step 3", now));

        Assert.Equal(3, spoken.Count);
    }

    [Fact]
    public async Task SpokenSink_EmptyStatusText_DoesNotSpeak()
    {
        var session = new FakeCallSession();
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };
        var sink = new SpokenToolProgressSink(session, tts);

        await sink.EmitAsync(new ToolProgressUpdate("c1", 25, null, DateTimeOffset.UtcNow));
        await sink.EmitAsync(new ToolProgressUpdate("c1", 50, "", DateTimeOffset.UtcNow));

        Assert.Empty(spoken);
    }

    [Fact]
    public async Task StreamingToolRunner_HandlerThrows_ReturnsFailure()
    {
        var sink = new RecordingToolProgressSink();
        StreamingToolHandler handler = (_, _, _) => throw new InvalidOperationException("boom");

        var result = await StreamingToolRunner.RunAsync(
            new ToolInvocation("c1", "x", "{}"), handler, sink);

        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Error);
    }

    private sealed class FakeCallSession : ICallSession
    {
        public CallInfo  Info   { get; } = new("c", CallDirection.Inbound, "+1", "+2", "fake", CallMediaFormat.Pcm24000, DateTimeOffset.UtcNow);
        public CallStatus Status => CallStatus.Active;
        public event EventHandler<CallStatus>? StatusChanged { add { } remove { } }

        public async IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public ValueTask SendDtmfAsync(string digits, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask TransferAsync(string t, TransferMode m, string? b = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask HangUpAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
