// Contracts.cs
//
// (3.3.0) Carrier-agnostic contracts for streaming realtime AI
// services. Five vendors implement these: OpenAI Realtime, Gemini Live,
// AWS Nova Sonic, ElevenLabs Conversational, Ultravox. Each vendor's
// WebSocket envelope differs; the IRealtimeService implementation
// translates between vendor JSON frames and these contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Realtime;

/// <summary>(3.3.0) Audio format used in realtime sessions.</summary>
public enum RealtimeAudioFormat
{
    /// <summary>16-bit linear PCM, mono, 16 kHz.</summary>
    Pcm16k,
    /// <summary>16-bit linear PCM, mono, 24 kHz.</summary>
    Pcm24k,
    /// <summary>G.711 μ-law, mono, 8 kHz (carrier-native).</summary>
    Mulaw8k,
}

/// <summary>(3.3.0) Direction of audio in a realtime session.</summary>
public enum RealtimeDirection { Inbound, Outbound }

/// <summary>(3.3.0) Configuration for opening a realtime session.</summary>
/// <param name="Model">Vendor-specific model id (e.g. <c>gpt-4o-realtime-preview-2024-12-17</c>).</param>
/// <param name="VoiceId">Vendor voice id (e.g. <c>alloy</c> for OpenAI, <c>Aoede</c> for Gemini).</param>
/// <param name="SystemPrompt">Persona / instructions that shape the assistant's responses.</param>
/// <param name="AudioFormat">Wire audio format. The host must transcode to/from this if the carrier differs.</param>
/// <param name="LanguageHint">ISO language hint (e.g. <c>en-US</c>); null = auto-detect.</param>
/// <param name="Tools">Optional list of tool definitions exposed to the model.</param>
public sealed record RealtimeSessionConfig(
    string                       Model,
    string?                      VoiceId         = null,
    string?                      SystemPrompt    = null,
    RealtimeAudioFormat          AudioFormat     = RealtimeAudioFormat.Pcm24k,
    string?                      LanguageHint    = null,
    IReadOnlyList<RealtimeTool>? Tools           = null);

/// <summary>(3.3.0) One tool the model can call.</summary>
/// <param name="Name">Tool name as the model sees it.</param>
/// <param name="Description">Human description of when to call this.</param>
/// <param name="JsonSchema">JSON schema for the tool's input arguments.</param>
public sealed record RealtimeTool(string Name, string Description, string JsonSchema);

/// <summary>(3.3.0) One audio frame in a realtime session.</summary>
public sealed record RealtimeAudioFrame(
    ReadOnlyMemory<byte> Pcm,
    RealtimeAudioFormat  Format,
    TimeSpan             Offset);

/// <summary>(3.3.0) Discriminated union of events emitted by the vendor session.</summary>
public abstract record RealtimeEvent(DateTimeOffset At);

/// <summary>Caller speech started.</summary>
public sealed record SpeechStartedEvent(DateTimeOffset At)                    : RealtimeEvent(At);

/// <summary>Caller speech ended (model is now processing).</summary>
public sealed record SpeechEndedEvent(DateTimeOffset At)                      : RealtimeEvent(At);

/// <summary>Partial transcript of caller speech.</summary>
public sealed record TranscriptDeltaEvent(DateTimeOffset At, string Delta, RealtimeDirection Direction) : RealtimeEvent(At);

/// <summary>Full transcript of caller utterance (final).</summary>
public sealed record TranscriptFinalEvent(DateTimeOffset At, string Text, RealtimeDirection Direction)  : RealtimeEvent(At);

/// <summary>The model wants to call a tool.</summary>
public sealed record ToolCallEvent(DateTimeOffset At, string CallId, string ToolName, string ArgumentsJson) : RealtimeEvent(At);

/// <summary>The assistant turn is complete.</summary>
public sealed record TurnCompleteEvent(DateTimeOffset At)                     : RealtimeEvent(At);

/// <summary>Vendor reported an error mid-session.</summary>
public sealed record SessionErrorEvent(DateTimeOffset At, string Message)     : RealtimeEvent(At);

/// <summary>
/// (3.3.0) One open conversation with a realtime vendor. Audio flows
/// in both directions concurrently; control + transcripts surface as
/// <see cref="RealtimeEvent"/>s.
/// </summary>
public interface IRealtimeSession : IAsyncDisposable
{
    /// <summary>Session identifier from the vendor.</summary>
    string SessionId { get; }

    /// <summary>Inbound audio (from caller → us).</summary>
    IAsyncEnumerable<RealtimeAudioFrame> ReceiveAudioAsync(CancellationToken ct = default);

    /// <summary>Send one audio frame to the model.</summary>
    ValueTask SendAudioAsync(RealtimeAudioFrame frame, CancellationToken ct = default);

    /// <summary>Send a text turn to the model (no audio, e.g. for a TTS-only turn).</summary>
    ValueTask SendTextAsync(string text, CancellationToken ct = default);

    /// <summary>Reply to a tool call with its result.</summary>
    ValueTask SendToolResultAsync(string callId, string resultJson, CancellationToken ct = default);

    /// <summary>Cancel the current model response (e.g. on barge-in).</summary>
    ValueTask CancelResponseAsync(CancellationToken ct = default);

    /// <summary>Control + transcript events from the vendor.</summary>
    IAsyncEnumerable<RealtimeEvent> ReceiveEventsAsync(CancellationToken ct = default);
}

/// <summary>(3.3.0) Vendor connector — opens realtime sessions.</summary>
public interface IRealtimeService
{
    /// <summary>Vendor self-id (e.g. <c>openai-realtime</c>).</summary>
    string ProviderId { get; }

    /// <summary>True when credentials are present.</summary>
    bool IsConfigured { get; }

    /// <summary>Open one realtime session per the supplied config.</summary>
    ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default);
}
