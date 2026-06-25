// Circle33TestSessionTests.cs
//
// (3.3.0) Tests for the in-memory TestCallSession harness.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33TestSessionTests
{
    [Fact]
    public async Task ReceiveAudio_PopsInjectedFrames()
    {
        var session = new TestCallSession();
        var frame   = new AudioFrame(new byte[] { 1, 2 }, CallMediaFormat.Pcm16000, TimeSpan.Zero);
        session.InjectInboundAudio(frame);
        session.EndInboundStreams();

        var received = new System.Collections.Generic.List<AudioFrame>();
        await foreach (var f in session.ReceiveAudioAsync())
        {
            received.Add(f);
        }

        Assert.Single(received);
        Assert.Equal(frame.Pcm.ToArray(), received[0].Pcm.ToArray());
    }

    [Fact]
    public async Task ReceiveDtmf_PopsInjectedEvents()
    {
        var session = new TestCallSession();
        session.InjectInboundDtmf(new DtmfEvent('1', TimeSpan.FromMilliseconds(100), TimeSpan.Zero));
        session.EndInboundStreams();

        var received = new System.Collections.Generic.List<DtmfEvent>();
        await foreach (var d in session.ReceiveDtmfAsync())
        {
            received.Add(d);
        }

        Assert.Single(received);
        Assert.Equal('1', received[0].Digit);
    }

    [Fact]
    public async Task SendAudio_IsCapturedForAssertions()
    {
        var session = new TestCallSession();
        var frame   = new AudioFrame(new byte[] { 3, 4 }, CallMediaFormat.Pcm24000, TimeSpan.Zero);
        await session.SendAudioAsync(frame);

        Assert.Single(session.SentAudioFrames);
        Assert.Equal(CallMediaFormat.Pcm24000, session.SentAudioFrames[0].Format);
    }

    [Fact]
    public async Task SendDtmf_IsCapturedForAssertions()
    {
        var session = new TestCallSession();
        await session.SendDtmfAsync("123");
        Assert.Single(session.SentDtmf);
        Assert.Equal("123", session.SentDtmf[0]);
    }

    [Fact]
    public async Task HangUp_TransitionsStatus_EndsStreams()
    {
        var session = new TestCallSession();
        CallStatus? observed = null;
        session.StatusChanged += (_, s) => observed = s;

        await session.HangUpAsync();

        Assert.Equal(CallStatus.EndedByAgent, session.Status);
        Assert.Equal(CallStatus.EndedByAgent, observed);

        // Streams should be drained.
        var frames = await session.ReceiveAudioAsync().ToListAsync();
        Assert.Empty(frames);
    }

    [Fact]
    public async Task TransferAsync_TransitionsStatus()
    {
        var session = new TestCallSession();
        CallStatus? observed = null;
        session.StatusChanged += (_, s) => observed = s;

        await session.TransferAsync("+18005550199", TransferMode.Cold);

        Assert.Equal(CallStatus.Transferred, session.Status);
        Assert.Equal(CallStatus.Transferred, observed);
    }

    [Fact]
    public void TriggerStatusChange_FiresEvent()
    {
        var session = new TestCallSession();
        CallStatus? observed = null;
        session.StatusChanged += (_, s) => observed = s;

        session.TriggerStatusChange(CallStatus.EndedByCaller);

        Assert.Equal(CallStatus.EndedByCaller, observed);
    }

    [Fact]
    public void DefaultInfo_HasCarrierTest()
    {
        var session = new TestCallSession();
        Assert.Equal("test", session.Info.CarrierId);
    }
}

internal static class AsyncEnumExt
{
    public static async System.Threading.Tasks.Task<System.Collections.Generic.List<T>> ToListAsync<T>(this System.Collections.Generic.IAsyncEnumerable<T> src)
    {
        var list = new System.Collections.Generic.List<T>();
        await foreach (var item in src) list.Add(item);
        return list;
    }
}
