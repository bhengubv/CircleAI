// Circle33AgentHandoffTests.cs
//
// (3.3.0) Tests for mid-call multi-agent handoff.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33AgentHandoffTests
{
    private static readonly CallAgent Reception = new("reception", "Reception",
        SystemPrompt: "You greet callers.", GreetingText: "Hello, how can I help?");
    private static readonly CallAgent Billing = new("billing", "Billing",
        SystemPrompt: "You handle billing.", GreetingText: "I'm the billing specialist.");

    [Fact]
    public async Task Handoff_KnownAgent_Succeeds()
    {
        var o = new DefaultAgentHandoffOrchestrator(new[] { Reception, Billing });
        o.SetInitialAgent("reception");

        var session = new FakeCallSession();
        var greetingCalls = 0;
        BriefingSynthesiser tts = (text, ct) =>
        {
            greetingCalls++;
            Assert.Equal("I'm the billing specialist.", text);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2 });
        };

        var result = await o.HandoffAsync(session, "billing", tts);

        Assert.True(result.Succeeded);
        Assert.Equal("billing", o.CurrentAgent!.AgentId);
        Assert.Equal(1, greetingCalls);
        Assert.True(session.AudioSent);
    }

    [Fact]
    public async Task Handoff_UnknownAgent_Fails()
    {
        var o = new DefaultAgentHandoffOrchestrator(new[] { Reception });
        var session = new FakeCallSession();
        BriefingSynthesiser tts = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);

        var result = await o.HandoffAsync(session, "tier2", tts);

        Assert.False(result.Succeeded);
        Assert.Contains("not registered", result.FailureReason);
    }

    [Fact]
    public async Task Handoff_SameAgent_IsNoop_NoTtsCall()
    {
        var o = new DefaultAgentHandoffOrchestrator(new[] { Reception, Billing });
        o.SetInitialAgent("reception");

        var session = new FakeCallSession();
        var ttsCalls = 0;
        BriefingSynthesiser tts = (_, _) =>
        {
            ttsCalls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        var result = await o.HandoffAsync(session, "reception", tts);

        Assert.True(result.Succeeded);
        Assert.Equal(0, ttsCalls);
        Assert.False(session.AudioSent);
    }

    [Fact]
    public async Task Handoff_EmptyTargetAgentId_Fails()
    {
        var o = new DefaultAgentHandoffOrchestrator(new[] { Reception });
        BriefingSynthesiser tts = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
        var result = await o.HandoffAsync(new FakeCallSession(), "", tts);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void SetInitialAgent_UnknownAgent_Throws()
    {
        var o = new DefaultAgentHandoffOrchestrator();
        Assert.Throws<InvalidOperationException>(() => o.SetInitialAgent("ghost"));
    }

    [Fact]
    public void RegisterAgent_AddsToCatalog()
    {
        var o = new DefaultAgentHandoffOrchestrator();
        o.RegisterAgent(Reception);
        Assert.Contains("reception", o.AgentCatalog.Keys);
    }

    [Fact]
    public void RegisterAgent_NullAgent_Throws()
    {
        var o = new DefaultAgentHandoffOrchestrator();
        Assert.Throws<ArgumentNullException>(() => o.RegisterAgent(null!));
    }

    [Fact]
    public void RegisterAgent_EmptyId_Throws()
    {
        var o = new DefaultAgentHandoffOrchestrator();
        Assert.Throws<ArgumentException>(() =>
            o.RegisterAgent(new CallAgent("", "Empty", "prompt")));
    }

    [Fact]
    public async Task Handoff_NoGreeting_DoesNotCallTts()
    {
        var silent = new CallAgent("silent", "Silent", "no greeting", GreetingText: null);
        var o = new DefaultAgentHandoffOrchestrator(new[] { Reception, silent });
        o.SetInitialAgent("reception");

        var ttsCalls = 0;
        BriefingSynthesiser tts = (_, _) =>
        {
            ttsCalls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
        };

        var result = await o.HandoffAsync(new FakeCallSession(), "silent", tts);

        Assert.True(result.Succeeded);
        Assert.Equal(0, ttsCalls);
    }

    [Fact]
    public async Task Handoff_TtsThrows_StillSucceedsButLogs()
    {
        var o = new DefaultAgentHandoffOrchestrator(new[] { Reception, Billing });
        o.SetInitialAgent("reception");
        BriefingSynthesiser tts = (_, _) => throw new InvalidOperationException("tts gone");

        var result = await o.HandoffAsync(new FakeCallSession(), "billing", tts);

        Assert.True(result.Succeeded);
        Assert.Equal("billing", o.CurrentAgent!.AgentId);
    }

    private sealed class FakeCallSession : ICallSession
    {
        public CallInfo  Info   { get; } = new("c1", CallDirection.Inbound, "+1", "+2", "fake", CallMediaFormat.Pcm24000, DateTimeOffset.UtcNow);
        public CallStatus Status => CallStatus.Active;
        public bool AudioSent { get; private set; }
        public event EventHandler<CallStatus>? StatusChanged { add { } remove { } }

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
        public ValueTask TransferAsync(string t, TransferMode m, string? b = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask HangUpAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
