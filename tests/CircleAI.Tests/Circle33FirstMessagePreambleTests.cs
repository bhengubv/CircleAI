// Circle33FirstMessagePreambleTests.cs
//
// (3.3.0) Tests for first-message preamble.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33FirstMessagePreambleTests
{
    [Fact]
    public async Task SpeakAsync_ModelSlow_SpeaksPreamble()
    {
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };
        var resolver = new PromptVariableResolver().Set("name", "Acme");
        var preamble = new DefaultFirstMessagePreamble(
            new FirstMessagePreambleOptions("Thanks for calling {{name}}.", TimeSpan.FromMilliseconds(50)),
            resolver);

        await preamble.SpeakAsync(
            new FakeCallSession(),
            tts,
            Task.Delay(5000)); // model never finishes within test

        Assert.Single(spoken);
        Assert.Equal("Thanks for calling Acme.", spoken[0]);
    }

    [Fact]
    public async Task SpeakAsync_ModelFastWin_SkipsPreamble()
    {
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };
        var preamble = new DefaultFirstMessagePreamble(
            new FirstMessagePreambleOptions("greeting", TimeSpan.FromMilliseconds(500)));

        await preamble.SpeakAsync(
            new FakeCallSession(),
            tts,
            Task.CompletedTask); // model is already ready

        Assert.Empty(spoken);
    }

    [Fact]
    public async Task SpeakAsync_EmptyTemplate_DoesNotSpeak()
    {
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };
        var preamble = new DefaultFirstMessagePreamble(
            new FirstMessagePreambleOptions("   ", TimeSpan.FromMilliseconds(50)));

        await preamble.SpeakAsync(
            new FakeCallSession(),
            tts,
            Task.Delay(5000));

        Assert.Empty(spoken);
    }

    [Fact]
    public async Task SpeakAsync_TemplateWithUnresolvedVariable_UsesDefault()
    {
        var spoken = new List<string>();
        BriefingSynthesiser tts = (text, ct) =>
        {
            spoken.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };
        var resolver = new PromptVariableResolver(defaultMissing: "the company");
        var preamble = new DefaultFirstMessagePreamble(
            new FirstMessagePreambleOptions("Thanks for calling {{name}}.", TimeSpan.FromMilliseconds(50)),
            resolver);

        await preamble.SpeakAsync(new FakeCallSession(), tts, Task.Delay(5000));

        Assert.Single(spoken);
        Assert.Equal("Thanks for calling the company.", spoken[0]);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DefaultFirstMessagePreamble(null!));
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
