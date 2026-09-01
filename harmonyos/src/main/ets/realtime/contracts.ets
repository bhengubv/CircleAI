/**
 * realtime/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime/contracts.ts
//
// (3.3.0) Carrier-agnostic contracts for streaming realtime AI services
// (Contracts.cs). Five vendors implement these: OpenAI Realtime, Gemini Live,
// AWS Nova Sonic, ElevenLabs Conversational, Ultravox. Each vendor's WebSocket
// envelope differs; the IRealtimeService implementation translates between
// vendor JSON frames and these contracts.
//
// Type mappings (C# → TS):
//   ReadOnlyMemory<byte>     → Uint8Array
//   TimeSpan                 → number (milliseconds)
//   DateTimeOffset           → Date
//   IAsyncEnumerable<T>      → AsyncIterable<T> (async generators)
//   IAsyncDisposable         → { disposeAsync(): Promise<void> }
//   abstract record + subs   → discriminated union tagged by `kind`

/** (3.3.0) Audio format used in realtime sessions. Mirrors C# `RealtimeAudioFormat`. */
export enum RealtimeAudioFormat {
  /** 16-bit linear PCM, mono, 16 kHz. */
  Pcm16k = 0,
  /** 16-bit linear PCM, mono, 24 kHz. */
  Pcm24k = 1,
  /** G.711 μ-law, mono, 8 kHz (carrier-native). */
  Mulaw8k = 2,
}

/** (3.3.0) Direction of audio in a realtime session. Mirrors C# `RealtimeDirection`. */
export enum RealtimeDirection {
  Inbound = 0,
  Outbound = 1,
}

/** (3.3.0) One tool the model can call. Mirrors C# `RealtimeTool`. */
export interface RealtimeTool {
  /** Tool name as the model sees it. */
  readonly name: string;
  /** Human description of when to call this. */
  readonly description: string;
  /** JSON schema for the tool's input arguments. */
  readonly jsonSchema: string;
}

/** Constructs a {@link RealtimeTool}. */
export function realtimeTool(name: string, description: string, jsonSchema: string): RealtimeTool {
  return { name, description, jsonSchema };
}

/**
 * (3.3.0) Configuration for opening a realtime session. Mirrors C#
 * `RealtimeSessionConfig`.
 */
export interface RealtimeSessionConfig {
  /** Vendor-specific model id (e.g. `gpt-4o-realtime-preview-2024-12-17`). */
  readonly model: string;
  /** Vendor voice id (e.g. `alloy` for OpenAI, `Aoede` for Gemini). */
  readonly voiceId: string | null;
  /** Persona / instructions that shape the assistant's responses. */
  readonly systemPrompt: string | null;
  /** Wire audio format. The host must transcode to/from this if the carrier differs. */
  readonly audioFormat: RealtimeAudioFormat;
  /** ISO language hint (e.g. `en-US`); null = auto-detect. */
  readonly languageHint: string | null;
  /** Optional list of tool definitions exposed to the model. */
  readonly tools: readonly RealtimeTool[] | null;
}

/** Constructs a {@link RealtimeSessionConfig} with the C# record defaults. */
export function realtimeSessionConfig(
  model: string,
  overrides: Partial<Omit<RealtimeSessionConfig, "model">> = {},
): RealtimeSessionConfig {
  return {
    model,
    voiceId: overrides.voiceId ?? null,
    systemPrompt: overrides.systemPrompt ?? null,
    audioFormat: overrides.audioFormat ?? RealtimeAudioFormat.Pcm24k,
    languageHint: overrides.languageHint ?? null,
    tools: overrides.tools ?? null,
  };
}

/** (3.3.0) One audio frame in a realtime session. Mirrors C# `RealtimeAudioFrame`. */
export interface RealtimeAudioFrame {
  readonly pcm: Uint8Array;
  readonly format: RealtimeAudioFormat;
  /** Offset from session start, in milliseconds (C# TimeSpan). */
  readonly offsetMs: number;
}

/** Constructs a {@link RealtimeAudioFrame}. */
export function realtimeAudioFrame(pcm: Uint8Array, format: RealtimeAudioFormat, offsetMs: number): RealtimeAudioFrame {
  return { pcm, format, offsetMs };
}

/** Discriminator tags for the {@link RealtimeEvent} union. */
export enum RealtimeEventKind {
  SpeechStarted = "speech-started",
  SpeechEnded = "speech-ended",
  TranscriptDelta = "transcript-delta",
  TranscriptFinal = "transcript-final",
  ToolCall = "tool-call",
  TurnComplete = "turn-complete",
  SessionError = "session-error",
}

interface RealtimeEventBase {
  readonly at: Date;
}

/** Caller speech started. Mirrors C# `SpeechStartedEvent`. */
export interface SpeechStartedEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.SpeechStarted;
}

/** Caller speech ended (model is now processing). Mirrors C# `SpeechEndedEvent`. */
export interface SpeechEndedEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.SpeechEnded;
}

/** Partial transcript. Mirrors C# `TranscriptDeltaEvent`. */
export interface TranscriptDeltaEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.TranscriptDelta;
  readonly delta: string;
  readonly direction: RealtimeDirection;
}

/** Full transcript of an utterance (final). Mirrors C# `TranscriptFinalEvent`. */
export interface TranscriptFinalEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.TranscriptFinal;
  readonly text: string;
  readonly direction: RealtimeDirection;
}

/** The model wants to call a tool. Mirrors C# `ToolCallEvent`. */
export interface ToolCallEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.ToolCall;
  readonly callId: string;
  readonly toolName: string;
  readonly argumentsJson: string;
}

/** The assistant turn is complete. Mirrors C# `TurnCompleteEvent`. */
export interface TurnCompleteEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.TurnComplete;
}

/** Vendor reported an error mid-session. Mirrors C# `SessionErrorEvent`. */
export interface SessionErrorEvent extends RealtimeEventBase {
  readonly kind: RealtimeEventKind.SessionError;
  readonly message: string;
}

/** (3.3.0) Discriminated union of events emitted by the vendor session. Mirrors C# `RealtimeEvent`. */
export type RealtimeEvent =
  | SpeechStartedEvent
  | SpeechEndedEvent
  | TranscriptDeltaEvent
  | TranscriptFinalEvent
  | ToolCallEvent
  | TurnCompleteEvent
  | SessionErrorEvent;

/** Constructs a {@link SpeechStartedEvent}. */
export function speechStartedEvent(at: Date): SpeechStartedEvent {
  return { kind: RealtimeEventKind.SpeechStarted, at };
}

/** Constructs a {@link SpeechEndedEvent}. */
export function speechEndedEvent(at: Date): SpeechEndedEvent {
  return { kind: RealtimeEventKind.SpeechEnded, at };
}

/** Constructs a {@link TranscriptDeltaEvent}. */
export function transcriptDeltaEvent(at: Date, delta: string, direction: RealtimeDirection): TranscriptDeltaEvent {
  return { kind: RealtimeEventKind.TranscriptDelta, at, delta, direction };
}

/** Constructs a {@link TranscriptFinalEvent}. */
export function transcriptFinalEvent(at: Date, text: string, direction: RealtimeDirection): TranscriptFinalEvent {
  return { kind: RealtimeEventKind.TranscriptFinal, at, text, direction };
}

/** Constructs a {@link ToolCallEvent}. */
export function toolCallEvent(at: Date, callId: string, toolName: string, argumentsJson: string): ToolCallEvent {
  return { kind: RealtimeEventKind.ToolCall, at, callId, toolName, argumentsJson };
}

/** Constructs a {@link TurnCompleteEvent}. */
export function turnCompleteEvent(at: Date): TurnCompleteEvent {
  return { kind: RealtimeEventKind.TurnComplete, at };
}

/** Constructs a {@link SessionErrorEvent}. */
export function sessionErrorEvent(at: Date, message: string): SessionErrorEvent {
  return { kind: RealtimeEventKind.SessionError, at, message };
}

/**
 * (3.3.0) One open conversation with a realtime vendor. Audio flows in both
 * directions concurrently; control + transcripts surface as
 * {@link RealtimeEvent}s. Mirrors C# `IRealtimeSession : IAsyncDisposable`.
 */
export interface IRealtimeSession {
  /** Session identifier from the vendor. */
  readonly sessionId: string;

  /** Inbound audio (from caller → us). */
  receiveAudioAsync(signal?: AbortSignal): AsyncIterable<RealtimeAudioFrame>;

  /** Send one audio frame to the model. */
  sendAudioAsync(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void>;

  /** Send a text turn to the model (no audio, e.g. for a TTS-only turn). */
  sendTextAsync(text: string, signal?: AbortSignal): Promise<void>;

  /** Reply to a tool call with its result. */
  sendToolResultAsync(callId: string, resultJson: string, signal?: AbortSignal): Promise<void>;

  /** Cancel the current model response (e.g. on barge-in). */
  cancelResponseAsync(signal?: AbortSignal): Promise<void>;

  /** Control + transcript events from the vendor. */
  receiveEventsAsync(signal?: AbortSignal): AsyncIterable<RealtimeEvent>;

  /** Release session resources (IAsyncDisposable). */
  disposeAsync(): Promise<void>;
}

/** (3.3.0) Vendor connector — opens realtime sessions. Mirrors C# `IRealtimeService`. */
export interface IRealtimeService {
  /** Vendor self-id (e.g. `openai-realtime`). */
  readonly providerId: string;

  /** True when credentials are present. */
  readonly isConfigured: boolean;

  /** Open one realtime session per the supplied config. */
  startSessionAsync(config: RealtimeSessionConfig, signal?: AbortSignal): Promise<IRealtimeSession>;
}

/** Sample rate (Hz) for a {@link RealtimeAudioFormat}. Mirrors the C# `SampleRateOf` switch. */
export function sampleRateOf(format: RealtimeAudioFormat): number {
  switch (format) {
    case RealtimeAudioFormat.Pcm16k:
      return 16_000;
    case RealtimeAudioFormat.Pcm24k:
      return 24_000;
    case RealtimeAudioFormat.Mulaw8k:
      return 8_000;
    default:
      return 16_000;
  }
}
