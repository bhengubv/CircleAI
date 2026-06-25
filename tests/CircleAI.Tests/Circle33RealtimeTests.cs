// Circle33RealtimeTests.cs
//
// (3.3.0) Tests for the 5 realtime vendor connectors + the shared
// RealtimeWebSocketSession event parser. Uses a fake transport to
// verify behaviour without real WebSockets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CircleAI.Realtime;
using CircleAI.Realtime.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33RealtimeTests
{
    private static readonly RealtimeSessionConfig BasicConfig = new(
        Model:        "test-model",
        VoiceId:      "alloy",
        SystemPrompt: "be terse",
        AudioFormat:  RealtimeAudioFormat.Pcm24k);

    // ===== OpenAI Realtime =====

    [Fact]
    public void OpenAi_NotConfigured_WhenKeyMissing()
    {
        var svc = new OpenAiRealtimeService(new OpenAiRealtimeOptions());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task OpenAi_Throws_WhenNotConfigured()
    {
        var svc = new OpenAiRealtimeService(new OpenAiRealtimeOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartSessionAsync(BasicConfig).AsTask());
    }

    [Fact]
    public async Task OpenAi_Configured_PassesAuthAndBetaHeader()
    {
        var factory = new RecordingTransportFactory();
        var svc = new OpenAiRealtimeService(
            new OpenAiRealtimeOptions { ApiKey = "k" }, factory);

        await using var session = await svc.StartSessionAsync(BasicConfig);

        Assert.Equal("openai-realtime", svc.ProviderId);
        Assert.NotNull(session.SessionId);
        Assert.Equal("Bearer k", factory.LastHeaders!["Authorization"]);
        Assert.Equal("realtime=v1", factory.LastHeaders["OpenAI-Beta"]);
        Assert.Contains("model=test-model", factory.LastEndpoint!.ToString());
    }

    // ===== Gemini Live =====

    [Fact]
    public void Gemini_NotConfigured_WhenKeyMissing()
    {
        var svc = new GeminiLiveService(new GeminiLiveOptions());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task Gemini_Throws_WhenNotConfigured()
    {
        var svc = new GeminiLiveService(new GeminiLiveOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartSessionAsync(BasicConfig).AsTask());
    }

    [Fact]
    public async Task Gemini_Configured_AppendsKeyToQuery()
    {
        var factory = new RecordingTransportFactory();
        var svc = new GeminiLiveService(new GeminiLiveOptions { ApiKey = "secret" }, factory);

        await using var session = await svc.StartSessionAsync(BasicConfig);

        Assert.Equal("gemini-live", svc.ProviderId);
        Assert.Contains("key=secret", factory.LastEndpoint!.ToString());
    }

    // ===== AWS Nova Sonic =====

    [Fact]
    public void NovaSonic_NotConfigured_WhenCredsMissing()
    {
        var svc = new NovaSonicService(new NovaSonicOptions());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task NovaSonic_Throws_WhenNotConfigured()
    {
        var svc = new NovaSonicService(new NovaSonicOptions { AccessKeyId = "x" });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartSessionAsync(BasicConfig).AsTask());
    }

    [Fact]
    public async Task NovaSonic_Configured_PutsCredsInHeadersForSigV4()
    {
        var factory = new RecordingTransportFactory();
        var svc = new NovaSonicService(
            new NovaSonicOptions
            {
                AccessKeyId     = "AKIA",
                SecretAccessKey = "SECRET",
                SessionToken    = "TOKEN",
                Region          = "us-west-2",
            },
            factory);

        await using var session = await svc.StartSessionAsync(BasicConfig);

        Assert.Equal("aws-nova-sonic", svc.ProviderId);
        Assert.Equal("AKIA",   factory.LastHeaders!["X-Amz-Access-Key"]);
        Assert.Equal("SECRET", factory.LastHeaders["X-Amz-Secret-Key"]);
        Assert.Equal("TOKEN",  factory.LastHeaders["X-Amz-Security-Token"]);
        Assert.Equal("us-west-2", factory.LastHeaders["X-Amz-Region"]);
        Assert.Contains("us-west-2.amazonaws.com", factory.LastEndpoint!.ToString());
    }

    // ===== ElevenLabs Conv =====

    [Fact]
    public void ElevenLabsConv_NotConfigured_WhenAgentIdMissing()
    {
        var svc = new ElevenLabsConvService(new ElevenLabsConvOptions { ApiKey = "k" });
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public void ElevenLabsConv_NotConfigured_WhenKeyMissing()
    {
        var svc = new ElevenLabsConvService(new ElevenLabsConvOptions { AgentId = "a" });
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task ElevenLabsConv_Configured_PassesAgentIdAndKey()
    {
        var factory = new RecordingTransportFactory();
        var svc = new ElevenLabsConvService(
            new ElevenLabsConvOptions { ApiKey = "k", AgentId = "agent-1" }, factory);

        await using var session = await svc.StartSessionAsync(BasicConfig);

        Assert.Equal("elevenlabs-conv", svc.ProviderId);
        Assert.Equal("k", factory.LastHeaders!["xi-api-key"]);
        Assert.Contains("agent_id=agent-1", factory.LastEndpoint!.ToString());
    }

    // ===== Ultravox =====

    [Fact]
    public void Ultravox_NotConfigured_WhenKeyMissing()
    {
        var http = new HttpClient();
        var svc = new UltravoxService(http, new UltravoxOptions());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task Ultravox_Throws_WhenNotConfigured()
    {
        var http = new HttpClient();
        var svc = new UltravoxService(http, new UltravoxOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartSessionAsync(BasicConfig).AsTask());
    }

    [Fact]
    public async Task Ultravox_Configured_FetchesJoinUrlThenConnectsTransport()
    {
        var handler = new RecordingHandler((_ => true,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"joinUrl":"wss://ultravox.example/call/abc"}""",
                    Encoding.UTF8, "application/json"),
            }));
        var http = new HttpClient(handler);
        var factory = new RecordingTransportFactory();
        var svc = new UltravoxService(http, new UltravoxOptions { ApiKey = "k" }, factory);

        await using var session = await svc.StartSessionAsync(BasicConfig);

        Assert.Equal("ultravox", svc.ProviderId);
        Assert.Equal(new Uri("wss://ultravox.example/call/abc"), factory.LastEndpoint);
        Assert.Equal("k", handler.Requests[0].Headers.GetValues("X-API-Key").Single());
    }

    // ===== Session behaviour =====

    [Fact]
    public async Task Session_SendAudio_ForwardsAsBinary()
    {
        var factory = new RecordingTransportFactory();
        var svc = new OpenAiRealtimeService(new OpenAiRealtimeOptions { ApiKey = "k" }, factory);
        await using var session = await svc.StartSessionAsync(BasicConfig);

        var pcm = new byte[] { 1, 2, 3, 4 };
        await session.SendAudioAsync(new RealtimeAudioFrame(pcm, RealtimeAudioFormat.Pcm24k, TimeSpan.Zero));

        Assert.Single(factory.LastTransport!.BinarySent);
        Assert.Equal(pcm, factory.LastTransport.BinarySent[0].ToArray());
    }

    [Fact]
    public async Task Session_SendText_WrapsInJsonEnvelope()
    {
        var factory = new RecordingTransportFactory();
        var svc = new OpenAiRealtimeService(new OpenAiRealtimeOptions { ApiKey = "k" }, factory);
        await using var session = await svc.StartSessionAsync(BasicConfig);

        await session.SendTextAsync("hello");

        var frame = factory.LastTransport!.TextSent.Single();
        Assert.Contains("\"type\":\"user.text\"", frame);
        Assert.Contains("\"text\":\"hello\"", frame);
    }

    [Fact]
    public async Task Session_SendToolResult_WrapsInJsonEnvelope()
    {
        var factory = new RecordingTransportFactory();
        var svc = new OpenAiRealtimeService(new OpenAiRealtimeOptions { ApiKey = "k" }, factory);
        await using var session = await svc.StartSessionAsync(BasicConfig);

        await session.SendToolResultAsync("call-1", """{"ok":true}""");

        var frame = factory.LastTransport!.TextSent.Single();
        Assert.Contains("\"type\":\"tool.result\"", frame);
        Assert.Contains("\"call_id\":\"call-1\"", frame);
    }

    [Fact]
    public async Task Session_CancelResponse_SendsCancelEnvelope()
    {
        var factory = new RecordingTransportFactory();
        var svc = new OpenAiRealtimeService(new OpenAiRealtimeOptions { ApiKey = "k" }, factory);
        await using var session = await svc.StartSessionAsync(BasicConfig);

        await session.CancelResponseAsync();

        Assert.Contains("response.cancel", factory.LastTransport!.TextSent.Single());
    }

    // ===== Event parser =====

    [Fact]
    public void ParseEvent_OpenAiSpeechStarted()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"type":"input_audio_buffer.speech_started"}""");
        Assert.IsType<SpeechStartedEvent>(ev);
    }

    [Fact]
    public void ParseEvent_OpenAiSpeechStopped()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"type":"input_audio_buffer.speech_stopped"}""");
        Assert.IsType<SpeechEndedEvent>(ev);
    }

    [Fact]
    public void ParseEvent_OpenAiTranscriptDelta_Inbound()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"type":"transcript.delta","delta":"hi"}""");
        var td = Assert.IsType<TranscriptDeltaEvent>(ev);
        Assert.Equal("hi", td.Delta);
        Assert.Equal(RealtimeDirection.Inbound, td.Direction);
    }

    [Fact]
    public void ParseEvent_OpenAiAudioTranscriptDelta_Outbound()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"type":"response.audio_transcript.delta","delta":"hello"}""");
        var td = Assert.IsType<TranscriptDeltaEvent>(ev);
        Assert.Equal("hello", td.Delta);
        Assert.Equal(RealtimeDirection.Outbound, td.Direction);
    }

    [Fact]
    public void ParseEvent_OpenAiToolCall()
    {
        var ev = RealtimeWebSocketSession.ParseEvent(
            """{"type":"response.function_call_arguments.done","call_id":"c1","name":"add","arguments":{"a":1}}""");
        var tc = Assert.IsType<ToolCallEvent>(ev);
        Assert.Equal("c1", tc.CallId);
        Assert.Equal("add", tc.ToolName);
        Assert.Contains("\"a\":1", tc.ArgumentsJson);
    }

    [Fact]
    public void ParseEvent_TurnComplete()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"type":"response.done"}""");
        Assert.IsType<TurnCompleteEvent>(ev);
    }

    [Fact]
    public void ParseEvent_Error()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"type":"error","error":{"message":"bad thing"}}""");
        var er = Assert.IsType<SessionErrorEvent>(ev);
        Assert.Equal("bad thing", er.Message);
    }

    [Fact]
    public void ParseEvent_GeminiTurnComplete()
    {
        var ev = RealtimeWebSocketSession.ParseEvent("""{"serverContent":{"turnComplete":true}}""");
        Assert.IsType<TurnCompleteEvent>(ev);
    }

    [Fact]
    public void ParseEvent_GeminiModelTurnText()
    {
        var ev = RealtimeWebSocketSession.ParseEvent(
            """{"serverContent":{"modelTurn":{"parts":[{"text":"hi from gemini"}]}}}""");
        var td = Assert.IsType<TranscriptDeltaEvent>(ev);
        Assert.Equal("hi from gemini", td.Delta);
        Assert.Equal(RealtimeDirection.Outbound, td.Direction);
    }

    [Fact]
    public void ParseEvent_UnknownShape_ReturnsNull()
    {
        Assert.Null(RealtimeWebSocketSession.ParseEvent("""{"some":"other"}"""));
        Assert.Null(RealtimeWebSocketSession.ParseEvent(""));
    }

    // ===== Null defaults =====

    [Fact]
    public void NullRealtimeService_IsConfiguredFalse_AndThrowsOnStart()
    {
        var svc = NullRealtimeService.Instance;
        Assert.False(svc.IsConfigured);
        Assert.Equal("null", svc.ProviderId);
        Assert.Throws<InvalidOperationException>(() => svc.StartSessionAsync(BasicConfig).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void DI_AddCircleAiRealtime_RegistersNullDefault()
    {
        var services = new ServiceCollection();
        services.AddCircleAiRealtime();
        using var sp = services.BuildServiceProvider();
        Assert.Equal("null", sp.GetRequiredService<IRealtimeService>().ProviderId);
    }

    [Fact]
    public void DI_AddOpenAiRealtime_OverridesNullDefault()
    {
        var services = new ServiceCollection();
        services.AddCircleAiRealtime();
        services.AddOpenAiRealtime(_ => new OpenAiRealtimeOptions { ApiKey = "x" });
        using var sp = services.BuildServiceProvider();
        Assert.Equal("openai-realtime", sp.GetRequiredService<IRealtimeService>().ProviderId);
    }

    // ===== Fake transport + handler =====

    private sealed class RecordingTransportFactory : IRealtimeTransportFactory
    {
        public Uri?                                LastEndpoint  { get; private set; }
        public IReadOnlyDictionary<string, string>? LastHeaders   { get; private set; }
        public RecordingTransport?                 LastTransport { get; private set; }

        public ValueTask<IRealtimeTransport> ConnectAsync(
            Uri                                  endpoint,
            IReadOnlyDictionary<string, string>? headers,
            CancellationToken                    ct = default)
        {
            LastEndpoint  = endpoint;
            LastHeaders   = headers;
            LastTransport = new RecordingTransport();
            return ValueTask.FromResult<IRealtimeTransport>(LastTransport);
        }
    }

    private sealed class RecordingTransport : IRealtimeTransport
    {
        public List<string>                  TextSent   { get; } = new();
        public List<ReadOnlyMemory<byte>>    BinarySent { get; } = new();
        public bool                          IsOpen     { get; private set; } = true;

        public ValueTask SendTextAsync(string text, CancellationToken ct = default)
        {
            TextSent.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendBinaryAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            BinarySent.Add(bytes);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReceiveTextAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveBinaryAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask CloseAsync(CancellationToken ct = default)
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
