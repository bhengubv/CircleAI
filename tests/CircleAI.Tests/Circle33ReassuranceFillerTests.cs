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

        // THE WORK ENDS WHEN THE FILLER HAS SPOKEN, not after a fixed 100 ms.
        //
        // It used to race: a 30 ms filler timer against 100 ms of pretend work, so
        // the test passed only if the timer got a thread inside a 70 ms window.
        // Under the full suite it often does not, the filler never speaks, and the
        // assertion fails for a reason that has nothing to do with rotation.
        //
        // Now each run BLOCKS until this run's filler has been said, which is the
        // precondition the assertion actually depends on. Slow machine, fast
        // machine, loaded machine — same result, and the rotation is what is being
        // tested rather than the scheduler.
        for (int i = 0; i < 3; i++)
        {
            var spokeThisRun = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var before = 0;
            lock (seen) before = seen.Count;

            BriefingSynthesiser watching = (text, ct) =>
            {
                lock (seen) seen.Add(text);
                spokeThisRun.TrySetResult();
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
            };

            await filler.RunWithFillerAsync(
                async ct =>
                {
                    await Eventually.CompletesAsync(spokeThisRun.Task,
                        $"the filler to speak on run {i + 1}");
                    return i;
                },
                session, watching);

            lock (seen)
                Assert.True(seen.Count > before, $"run {i + 1} produced no filler");
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
