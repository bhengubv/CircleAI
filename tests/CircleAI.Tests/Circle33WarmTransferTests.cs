// Circle33WarmTransferTests.cs
//
// (3.3.0) Tests for warm-transfer orchestration.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33WarmTransferTests
{
    [Fact]
    public async Task Execute_DialsTargetSpeaksBriefingThenTransfersCaller()
    {
        var carrier = new FakeCarrier();
        var ttsCalls = 0;
        BriefingSynthesiser tts = (text, ct) =>
        {
            ttsCalls++;
            Assert.Equal("Customer is calling about an outage.", text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3, 4 });
        };
        var orchestrator = new DefaultWarmTransferOrchestrator(carrier, tts);

        var source = new FakeCallSession("source-1", to: "+15555550100");
        var result = await orchestrator.ExecuteAsync(new WarmTransferRequest(
            SourceSession:   source,
            TargetNumber:    "+15555550200",
            BriefingText:    "Customer is calling about an outage.",
            BridgeStreamUrl: new Uri("wss://example.com/bridge")));

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureReason);
        Assert.Equal(1, ttsCalls);
        Assert.Equal("+15555550200", carrier.LastDialedTo);
        Assert.True(source.TransferCalled);
        Assert.Equal("+15555550200", source.LastTransferTarget);
        Assert.Equal(TransferMode.Cold, source.LastTransferMode);
    }

    [Fact]
    public async Task Execute_DialFailure_ReportsFailure()
    {
        var carrier = new FakeCarrier { ThrowOnDial = true };
        BriefingSynthesiser tts = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
        var orchestrator = new DefaultWarmTransferOrchestrator(carrier, tts);

        var result = await orchestrator.ExecuteAsync(new WarmTransferRequest(
            SourceSession:   new FakeCallSession("source", "+15555550100"),
            TargetNumber:    "+15555550200",
            BriefingText:    "hi",
            BridgeStreamUrl: new Uri("wss://example.com/bridge")));

        Assert.False(result.Succeeded);
        Assert.Contains("Failed to dial", result.FailureReason);
    }

    [Fact]
    public async Task Execute_BriefingFailure_HangsUpBridgeLeg()
    {
        var carrier = new FakeCarrier();
        BriefingSynthesiser tts = (_, _) => throw new InvalidOperationException("tts down");
        var orchestrator = new DefaultWarmTransferOrchestrator(carrier, tts);

        var result = await orchestrator.ExecuteAsync(new WarmTransferRequest(
            SourceSession:   new FakeCallSession("source", "+15555550100"),
            TargetNumber:    "+15555550200",
            BriefingText:    "hi",
            BridgeStreamUrl: new Uri("wss://example.com/bridge")));

        Assert.False(result.Succeeded);
        Assert.Contains("brief target", result.FailureReason);
        Assert.True(carrier.LastBridgeLeg!.WasHungUp);
    }

    [Fact]
    public async Task Execute_MissingTargetNumber_FailsValidation()
    {
        var orchestrator = new DefaultWarmTransferOrchestrator(
            new FakeCarrier(),
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty));

        var result = await orchestrator.ExecuteAsync(new WarmTransferRequest(
            SourceSession:   new FakeCallSession("s", "+15555550100"),
            TargetNumber:    "",
            BriefingText:    "hi",
            BridgeStreamUrl: new Uri("wss://example.com/bridge")));

        Assert.False(result.Succeeded);
        Assert.Contains("TargetNumber", result.FailureReason);
    }

    [Fact]
    public async Task Execute_NullSource_FailsValidation()
    {
        var orchestrator = new DefaultWarmTransferOrchestrator(
            new FakeCarrier(),
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty));

        var result = await orchestrator.ExecuteAsync(new WarmTransferRequest(
            SourceSession:   null!,
            TargetNumber:    "+15555550200",
            BriefingText:    "hi",
            BridgeStreamUrl: new Uri("wss://example.com/bridge")));

        Assert.False(result.Succeeded);
    }

    private sealed class FakeCarrier : ITelephonyCarrier
    {
        public string CarrierId   => "fake";
        public bool   IsConfigured => true;
        public bool   ThrowOnDial { get; set; }
        public string? LastDialedTo { get; private set; }
        public FakeCallSession? LastBridgeLeg { get; private set; }

        public ValueTask<ProvisionedNumber> ProvisionNumberAsync(string c, string? a = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask ConfigureInboundWebhookAsync(string p, Uri u, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<ICallSession> DialAsync(string fromNumber, string toNumber, Uri streamUrl,
            OutboundDialOptions? options = null, CancellationToken ct = default)
        {
            if (ThrowOnDial) throw new InvalidOperationException("dial blocked");
            LastDialedTo  = toNumber;
            LastBridgeLeg = new FakeCallSession("bridge", toNumber);
            return ValueTask.FromResult<ICallSession>(LastBridgeLeg);
        }

        public ValueTask<IReadOnlyList<ProvisionedNumber>> ListNumbersAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeCallSession : ICallSession
    {
        public FakeCallSession(string id, string to)
        {
            Info = new CallInfo(id, CallDirection.Inbound, "+18005550100", to, "fake", CallMediaFormat.Pcm24000, DateTimeOffset.UtcNow);
        }

        public CallInfo  Info   { get; }
        public CallStatus Status => CallStatus.Active;
        public event EventHandler<CallStatus>? StatusChanged { add { } remove { } }

        public bool         TransferCalled    { get; private set; }
        public string?      LastTransferTarget { get; private set; }
        public TransferMode LastTransferMode  { get; private set; }
        public bool         WasHungUp         { get; private set; }
        public bool         AudioSent         { get; private set; }

        public async IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default)
        {
            AudioSent = true;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public ValueTask SendDtmfAsync(string digits, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask TransferAsync(string target, TransferMode mode, string? briefing = null, CancellationToken ct = default)
        {
            TransferCalled     = true;
            LastTransferTarget = target;
            LastTransferMode   = mode;
            return ValueTask.CompletedTask;
        }

        public ValueTask HangUpAsync(CancellationToken ct = default)
        {
            WasHungUp = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
