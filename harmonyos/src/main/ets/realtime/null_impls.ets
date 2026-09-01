/**
 * realtime/null_impls
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime/null_impls.ts
//
// (3.3.0) Null defaults so hosts stay green when no vendor is configured
// (NullImplementations.cs). NullRealtimeService throws on startSession with a
// clear "no vendor wired" message.

import type {
  IRealtimeService,
  IRealtimeSession,
  RealtimeAudioFrame,
  RealtimeEvent,
  RealtimeSessionConfig,
} from "./contracts";

/** (3.3.0) Throws on startSessionAsync; reports isConfigured=false. Mirrors C# `NullRealtimeService`. */
export class NullRealtimeService implements IRealtimeService {
  static readonly instance = new NullRealtimeService();

  get providerId(): string {
    return "null";
  }

  get isConfigured(): boolean {
    return false;
  }

  startSessionAsync(_config: RealtimeSessionConfig, _signal?: AbortSignal): Promise<IRealtimeSession> {
    throw new Error(
      "No realtime vendor is registered. Add CircleAI.Realtime.Cloud connectors (OpenAI, Gemini, Nova, ElevenLabs, Ultravox).",
    );
  }
}

/** (3.3.0) A session that yields nothing — fully muted. Mirrors C# `NullRealtimeSession`. */
export class NullRealtimeSession implements IRealtimeSession {
  get sessionId(): string {
    return "null";
  }

  // eslint-disable-next-line require-yield
  async *receiveAudioAsync(_signal?: AbortSignal): AsyncIterable<RealtimeAudioFrame> {
    await Promise.resolve();
    return;
  }

  sendAudioAsync(_frame: RealtimeAudioFrame, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  sendTextAsync(_text: string, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  sendToolResultAsync(_callId: string, _resultJson: string, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  cancelResponseAsync(_signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  // eslint-disable-next-line require-yield
  async *receiveEventsAsync(_signal?: AbortSignal): AsyncIterable<RealtimeEvent> {
    await Promise.resolve();
    return;
  }

  disposeAsync(): Promise<void> {
    return Promise.resolve();
  }
}
