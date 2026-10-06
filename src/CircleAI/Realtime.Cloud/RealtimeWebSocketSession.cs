// RealtimeWebSocketSession.cs
//
// (3.3.0) Concrete IRealtimeSession backed by an IRealtimeTransport.
// Vendor-specific JSON envelope translation lives in this class for
// now; if envelope shapes diverge enough we split per-vendor sessions.
//
// Today: the session forwards text frames as RealtimeEvent envelopes
// using a lenient parser that recognises common shapes (OpenAI Realtime,
// Gemini Live, ElevenLabs Conv). Binary frames become RealtimeAudioFrame
// in the format declared in RealtimeSessionConfig.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CircleAI.Realtime.Cloud;

public sealed class RealtimeWebSocketSession : IRealtimeSession
{
    private readonly IRealtimeTransport _transport;
    private readonly RealtimeSessionConfig _config;
    private readonly string _providerId;
    private readonly ILogger _logger;
    private readonly string _sessionId = Guid.NewGuid().ToString("n");

    public RealtimeWebSocketSession(
        IRealtimeTransport    transport,
        RealtimeSessionConfig config,
        string                providerId,
        ILogger               logger)
    {
        _transport  = transport;
        _config     = config;
        _providerId = providerId;
        _logger     = logger;
    }

    public string SessionId => _sessionId;

    public async IAsyncEnumerable<RealtimeAudioFrame> ReceiveAudioAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var frame in _transport.ReceiveBinaryAsync(ct).ConfigureAwait(false))
        {
            yield return new RealtimeAudioFrame(frame, _config.AudioFormat, Offset: TimeSpan.Zero);
        }
    }

    public ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken ct = default)
        => _transport.SendBinaryAsync(frame.Pcm, ct);

    public ValueTask SendTextAsync(string text, CancellationToken ct = default)
    {
        // Vendor-neutral envelope. Host-specific shims may translate.
        var json = JsonSerializer.Serialize(new
        {
            type     = "user.text",
            provider = _providerId,
            text     = text,
        });
        return _transport.SendTextAsync(json, ct);
    }

    public ValueTask SendToolResultAsync(string callId, string resultJson, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            type         = "tool.result",
            provider     = _providerId,
            call_id      = callId,
            result_json  = resultJson,
        });
        return _transport.SendTextAsync(json, ct);
    }

    public ValueTask CancelResponseAsync(CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { type = "response.cancel", provider = _providerId });
        return _transport.SendTextAsync(json, ct);
    }

    public async IAsyncEnumerable<RealtimeEvent> ReceiveEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var text in _transport.ReceiveTextAsync(ct).ConfigureAwait(false))
        {
            RealtimeEvent? ev = null;
            try
            {
                ev = ParseEvent(text);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not parse vendor frame on {Provider}; skipping", _providerId);
            }
            if (ev is not null) yield return ev;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _transport.CloseAsync().ConfigureAwait(false); } catch { }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Lenient cross-vendor JSON event parser.</summary>
    public static RealtimeEvent? ParseEvent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var at = DateTimeOffset.UtcNow;

        // OpenAI Realtime uses "type" = "input_audio_buffer.speech_started" etc.
        if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            var type = typeProp.GetString() ?? "";
            return type switch
            {
                "input_audio_buffer.speech_started" or "speech_started" => new SpeechStartedEvent(at),
                "input_audio_buffer.speech_stopped" or "speech_stopped" => new SpeechEndedEvent(at),

                "conversation.item.input_audio_transcription.delta" or "transcript.delta"
                    => new TranscriptDeltaEvent(at,
                           root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "",
                           RealtimeDirection.Inbound),

                "conversation.item.input_audio_transcription.completed" or "transcript.final"
                    => new TranscriptFinalEvent(at,
                           root.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" :
                           root.TryGetProperty("text",        out var x) ? x.GetString() ?? "" : "",
                           RealtimeDirection.Inbound),

                "response.audio_transcript.delta"
                    => new TranscriptDeltaEvent(at,
                           root.TryGetProperty("delta", out var d2) ? d2.GetString() ?? "" : "",
                           RealtimeDirection.Outbound),

                "response.audio_transcript.done"
                    => new TranscriptFinalEvent(at,
                           root.TryGetProperty("transcript", out var t2) ? t2.GetString() ?? "" : "",
                           RealtimeDirection.Outbound),

                "response.function_call_arguments.done" or "tool.call"
                    => new ToolCallEvent(at,
                           root.TryGetProperty("call_id", out var cid)  ? cid.GetString() ?? "" : "",
                           root.TryGetProperty("name",    out var nm)   ? nm.GetString() ?? ""  : "",
                           root.TryGetProperty("arguments", out var args) ? args.GetRawText() : "{}"),

                "response.done" or "turn.complete" => new TurnCompleteEvent(at),

                "error" => new SessionErrorEvent(at,
                              root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var em)
                                  ? em.GetString() ?? "" : json),

                _ => null,
            };
        }

        // Gemini Live emits { serverContent: { modelTurn: { parts: [{ text: "..." }] } } }
        if (root.TryGetProperty("serverContent", out var sc))
        {
            if (sc.TryGetProperty("turnComplete", out var tc) && tc.ValueKind == JsonValueKind.True)
            {
                return new TurnCompleteEvent(at);
            }
            if (sc.TryGetProperty("modelTurn", out var mt) &&
                mt.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var pt))
                    {
                        return new TranscriptDeltaEvent(at, pt.GetString() ?? "", RealtimeDirection.Outbound);
                    }
                }
            }
        }

        return null;
    }
}
