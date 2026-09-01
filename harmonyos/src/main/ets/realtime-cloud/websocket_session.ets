/**
 * realtime-cloud/websocket_session
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime-cloud/websocket_session.ts
//
// (3.3.0) Concrete IRealtimeSession backed by an IRealtimeTransport
// (RealtimeWebSocketSession.cs). Vendor-specific JSON envelope translation lives
// in this class for now; if envelope shapes diverge enough we split per-vendor
// sessions.
//
// Today: the session forwards text frames as RealtimeEvent envelopes using a
// lenient parser that recognises common shapes (OpenAI Realtime, Gemini Live,
// ElevenLabs Conv). Binary frames become RealtimeAudioFrame in the format
// declared in RealtimeSessionConfig.

import {
  RealtimeDirection,
  type IRealtimeSession,
  type RealtimeAudioFrame,
  type RealtimeEvent,
  type RealtimeSessionConfig,
  realtimeAudioFrame,
  sessionErrorEvent,
  speechEndedEvent,
  speechStartedEvent,
  toolCallEvent,
  transcriptDeltaEvent,
  transcriptFinalEvent,
  turnCompleteEvent,
} from "../realtime/index";
import { NullRealtimeLogger, type IRealtimeLogger, type IRealtimeTransport } from "./transport";

export class RealtimeWebSocketSession implements IRealtimeSession {
  private readonly transport: IRealtimeTransport;
  private readonly config: RealtimeSessionConfig;
  private readonly providerId: string;
  private readonly logger: IRealtimeLogger;
  readonly sessionId: string;

  constructor(
    transport: IRealtimeTransport,
    config: RealtimeSessionConfig,
    providerId: string,
    logger: IRealtimeLogger = NullRealtimeLogger.instance,
  ) {
    this.transport = transport;
    this.config = config;
    this.providerId = providerId;
    this.logger = logger;
    this.sessionId = uuidN();
  }

  async *receiveAudioAsync(signal?: AbortSignal): AsyncIterable<RealtimeAudioFrame> {
    for await (const frame of this.transport.receiveBinaryAsync(signal)) {
      yield realtimeAudioFrame(frame, this.config.audioFormat, 0);
    }
  }

  sendAudioAsync(frame: RealtimeAudioFrame, signal?: AbortSignal): Promise<void> {
    return this.transport.sendBinaryAsync(frame.pcm, signal);
  }

  sendTextAsync(text: string, signal?: AbortSignal): Promise<void> {
    // Vendor-neutral envelope. Host-specific shims may translate.
    const json = JSON.stringify({ type: "user.text", provider: this.providerId, text });
    return this.transport.sendTextAsync(json, signal);
  }

  sendToolResultAsync(callId: string, resultJson: string, signal?: AbortSignal): Promise<void> {
    const json = JSON.stringify({
      type: "tool.result",
      provider: this.providerId,
      call_id: callId,
      result_json: resultJson,
    });
    return this.transport.sendTextAsync(json, signal);
  }

  cancelResponseAsync(signal?: AbortSignal): Promise<void> {
    const json = JSON.stringify({ type: "response.cancel", provider: this.providerId });
    return this.transport.sendTextAsync(json, signal);
  }

  async *receiveEventsAsync(signal?: AbortSignal): AsyncIterable<RealtimeEvent> {
    for await (const text of this.transport.receiveTextAsync(signal)) {
      let ev: RealtimeEvent | null = null;
      try {
        ev = RealtimeWebSocketSession.parseEvent(text);
      } catch (ex) {
        this.logger.debug(`Could not parse vendor frame on ${this.providerId}; skipping`, ex);
      }
      if (ev !== null) yield ev;
    }
  }

  async disposeAsync(): Promise<void> {
    try {
      await this.transport.closeAsync();
    } catch {
      /* ignore */
    }
    await this.transport.disposeAsync();
  }

  /** Lenient cross-vendor JSON event parser. Mirrors C# `ParseEvent`. */
  static parseEvent(json: string): RealtimeEvent | null {
    if (json == null || json.trim().length === 0) return null;
    const root: unknown = JSON.parse(json);
    if (root == null || typeof root !== "object") return null;
    const obj = root as Record<string, unknown>;
    const at = new Date();

    // OpenAI Realtime uses "type" = "input_audio_buffer.speech_started" etc.
    if (typeof obj["type"] === "string") {
      const type = obj["type"];
      switch (type) {
        case "input_audio_buffer.speech_started":
        case "speech_started":
          return speechStartedEvent(at);

        case "input_audio_buffer.speech_stopped":
        case "speech_stopped":
          return speechEndedEvent(at);

        case "conversation.item.input_audio_transcription.delta":
        case "transcript.delta":
          return transcriptDeltaEvent(at, str(obj["delta"]), RealtimeDirection.Inbound);

        case "conversation.item.input_audio_transcription.completed":
        case "transcript.final": {
          const t = hasString(obj, "transcript") ? str(obj["transcript"]) : str(obj["text"]);
          return transcriptFinalEvent(at, t, RealtimeDirection.Inbound);
        }

        case "response.audio_transcript.delta":
          return transcriptDeltaEvent(at, str(obj["delta"]), RealtimeDirection.Outbound);

        case "response.audio_transcript.done":
          return transcriptFinalEvent(at, str(obj["transcript"]), RealtimeDirection.Outbound);

        case "response.function_call_arguments.done":
        case "tool.call":
          return toolCallEvent(at, str(obj["call_id"]), str(obj["name"]), rawJson(obj["arguments"], "{}"));

        case "response.done":
        case "turn.complete":
          return turnCompleteEvent(at);

        case "error": {
          const err = obj["error"];
          const message =
            err != null && typeof err === "object" && typeof (err as Record<string, unknown>)["message"] === "string"
              ? ((err as Record<string, unknown>)["message"] as string)
              : json;
          return sessionErrorEvent(at, message);
        }

        default:
          return null;
      }
    }

    // Gemini Live emits { serverContent: { modelTurn: { parts: [{ text: "..." }] } } }
    const sc = obj["serverContent"];
    if (sc != null && typeof sc === "object") {
      const scObj = sc as Record<string, unknown>;
      if (scObj["turnComplete"] === true) {
        return turnCompleteEvent(at);
      }
      const mt = scObj["modelTurn"];
      if (mt != null && typeof mt === "object") {
        const parts = (mt as Record<string, unknown>)["parts"];
        if (Array.isArray(parts)) {
          for (const part of parts) {
            if (part != null && typeof part === "object" && typeof (part as Record<string, unknown>)["text"] === "string") {
              return transcriptDeltaEvent(at, (part as Record<string, unknown>)["text"] as string, RealtimeDirection.Outbound);
            }
          }
        }
      }
    }

    return null;
  }
}

/** Read a JSON value as a string, defaulting to "" (mirrors `GetString() ?? ""`). */
function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

function hasString(obj: Record<string, unknown>, key: string): boolean {
  return typeof obj[key] === "string";
}

/** Re-serialise a parsed JSON value as raw text (mirrors `GetRawText()`); fallback when absent. */
function rawJson(v: unknown, fallback: string): string {
  return v === undefined ? fallback : JSON.stringify(v);
}

function uuidN(): string {
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  let out = "";
  for (let i = 0; i < 16; i++) out += bytes[i].toString(16).padStart(2, "0");
  return out;
}
