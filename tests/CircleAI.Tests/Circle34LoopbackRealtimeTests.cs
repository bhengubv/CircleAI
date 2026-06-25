// Circle34LoopbackRealtimeTests.cs
//
// (3.4.0) Unit tests for LoopbackRealtimeService — verifies the in-process
// realtime session implementation (loopback audio, transcript events,
// pluggable TTS, silence-sized audio).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Realtime;
using Xunit;

namespace CircleAI.Tests;

public class Circle34LoopbackRealtimeTests
{
    [Fact]
    public async Task StartSession_ReturnsRunningSession()
    {
        var svc = new LoopbackRealtimeService();
        await using var s = await svc.StartSessionAsync(NewConfig());
        Assert.NotEmpty(s.SessionId);
    }

    [Fact]
    public async Task SendText_EmitsTranscriptDeltaAndFinal_AndTurnComplete()
    {
        var svc = new LoopbackRealtimeService();
        await using var s = await svc.StartSessionAsync(NewConfig());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var events = new List<RealtimeEvent>();
        var pump = Task.Run(async () =>
        {
            await foreach (var e in s.ReceiveEventsAsync(cts.Token))
            {
                events.Add(e);
                if (e is TurnCompleteEvent) break;
            }
        }, cts.Token);

        await s.SendTextAsync("hello world");
        await pump;

        Assert.Contains(events, e => e is TranscriptDeltaEvent);
        Assert.Contains(events, e => e is TranscriptFinalEvent);
        Assert.Contains(events, e => e is TurnCompleteEvent);
    }

    [Fact]
    public async Task SendText_DefaultSilenceTextToAudio_ProducesRealPcmBytes()
    {
        var svc = new LoopbackRealtimeService();
        await using var s = await svc.StartSessionAsync(NewConfig());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var frames = new List<RealtimeAudioFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in s.ReceiveAudioAsync(cts.Token))
            {
                frames.Add(f);
                if (frames.Count >= 1) break;
            }
        }, cts.Token);

        await s.SendTextAsync("two words here");
        await pump;

        Assert.Single(frames);
        // 3 words × 80ms × 24kHz × 2 bytes = 11520 bytes
        Assert.True(frames[0].Pcm.Length > 0);
        // Should be all-zero PCM (silence)
        Assert.True(frames[0].Pcm.ToArray().All(b => b == 0));
    }

    [Fact]
    public async Task SendText_CustomTtsDelegate_IsInvoked()
    {
        var customCalled = false;
        var svc = new LoopbackRealtimeService((text, fmt, ct) =>
        {
            customCalled = true;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3, 4 });
        });
        await using var s = await svc.StartSessionAsync(NewConfig());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var pump = Task.Run(async () =>
        {
            await foreach (var f in s.ReceiveAudioAsync(cts.Token)) break;
        }, cts.Token);

        await s.SendTextAsync("custom tts please");
        await pump;
        Assert.True(customCalled);
    }

    [Fact]
    public async Task SendAudio_RingsBackAsOutbound()
    {
        var svc = new LoopbackRealtimeService();
        await using var s = await svc.StartSessionAsync(NewConfig());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var captured = new List<RealtimeAudioFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in s.ReceiveAudioAsync(cts.Token))
            {
                captured.Add(f);
                if (captured.Count >= 1) break;
            }
        }, cts.Token);

        var inbound = new byte[480];
        new Random(1).NextBytes(inbound);
        await s.SendAudioAsync(new RealtimeAudioFrame(inbound, RealtimeAudioFormat.Pcm24k, TimeSpan.Zero));
        await pump;
        Assert.Single(captured);
        Assert.Equal(inbound, captured[0].Pcm.ToArray());
    }

    private static RealtimeSessionConfig NewConfig()
        => new(Model: "loopback", AudioFormat: RealtimeAudioFormat.Pcm24k);
}
