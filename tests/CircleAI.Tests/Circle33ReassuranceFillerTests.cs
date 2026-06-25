// Circle33ReassuranceFillerTests.cs
//
// (3.3.0) Tests for reassurance fillers.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33ReassuranceFillerTests
{
    [Fact]
    public async Task FastWork_SkipsFiller()
    {
        var filler = new DefaultReassuranceFiller(new ReassuranceFillerOptions(
            ShortFillerAfter: TimeSpan.FromSeconds(5)));
        var session = new FakeCallSession();
        var ttsCalls = new ConcurrentBag<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            ttsCalls.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        var result = await filler.RunWithFillerAsync(
            async ct => { await Task.Delay(20, ct); return 42; },
            session, tts);

        Assert.Equal(42, result);
        Assert.Empty(ttsCalls);
    }

    [Fact]
    public async Task SlowWork_PlaysShortFiller()
    {
        var filler = new DefaultReassuranceFiller(new ReassuranceFillerOptions(
            ShortFillerAfter: TimeSpan.FromMilliseconds(50),
            LongFillerEvery:  TimeSpan.FromSeconds(5)));
        var session = new FakeCallSession();
        var ttsCalls = new ConcurrentBag<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            ttsCalls.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        var result = await filler.RunWithFillerAsync(
            async ct => { await Task.Delay(200, ct); return "ok"; },
            session, tts);

        Assert.Equal("ok", result);
        Assert.Contains(ttsCalls, t => ReassuranceVocabulary.Default.ShortFillers.Contains(t));
    }

    [Fact]
    public async Task VeryLongWork_PlaysLongFiller()
    {
        var filler = new DefaultReassuranceFiller(new ReassuranceFillerOptions(
            ShortFillerAfter: TimeSpan.FromMilliseconds(50),
            LongFillerEvery:  TimeSpan.FromMilliseconds(80)));
        var session = new FakeCallSession();
        var ttsCalls = new ConcurrentBag<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            ttsCalls.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        await filler.RunWithFillerAsync(
            async ct => { await Task.Delay(500, ct); return 0; },
            session, tts);

        Assert.Contains(ttsCalls, t => ReassuranceVocabulary.Default.LongFillers.Contains(t));
    }

    [Fact]
    public async Task WorkThrows_FillerStops()
    {
        var filler = new DefaultReassuranceFiller(new ReassuranceFillerOptions(
            ShortFillerAfter: TimeSpan.FromMilliseconds(30)));
        var session = new FakeCallSession();
        BriefingSynthesiser tts = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await filler.RunWithFillerAsync<int>(
                async ct => { await Task.Delay(200, ct); throw new InvalidOperationException("nope"); },
                session, tts));
    }

    [Fact]
    public async Task ShortFillers_RotateThroughVocabulary()
    {
        var vocab = new ReassuranceVocabulary(
            ShortFillers: new[] { "A", "B", "C" },
            LongFillers:  new[] { "X" });
        var filler = new DefaultReassuranceFiller(new ReassuranceFillerOptions(
            ShortFillerAfter: TimeSpan.FromMilliseconds(30),
            LongFillerEvery:  TimeSpan.FromSeconds(5),
            Vocabulary:       vocab));
        var session = new FakeCallSession();
        var seen = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            lock (seen) seen.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        // 3 sequential slow runs to advance the rotation.
        for (int i = 0; i < 3; i++)
        {
            await filler.RunWithFillerAsync(
                async ct => { await Task.Delay(100, ct); return i; },
                session, tts);
        }

        lock (seen)
        {
            Assert.Contains("A", seen);
            Assert.Contains("B", seen);
            Assert.Contains("C", seen);
        }
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
