// realtime/loopback.ts
//
// (3.3.0) Built-in, in-process IRealtimeService (LoopbackRealtimeService.cs) —
// connects audio in to audio out (loopback), surfaces speech-started/ended
// events from silence detection, and replies to sendTextAsync with a TTS-shaped
// PCM stream. Concrete vendor sessions (OpenAI / Gemini / etc.) ship in the
// realtime.cloud module; this one makes the realtime surface usable end-to-end
// out of the box for tests + dev.

import { AsyncQueue } from "./async_queue.js";
import {
  RealtimeAudioFormat,
  type IRealtimeService,
  type IRealtimeSession,
  type RealtimeAudioFrame,
  type RealtimeEvent,
  type RealtimeSessionConfig,
  realtimeAudioFrame,
  sampleRateOf,
  speechEndedEvent,
  speechStartedEvent,
  transcriptDeltaEvent,
  transcriptFinalEvent,
  turnCompleteEvent,
} from "./contracts.js";
import { RealtimeDirection } from "./contracts.js";

/**
 * (3.3.0) Synthesise outbound audio for text. Default produces real silence
 * frames matching the text's expected speech duration (~80ms per word). Hosts
 * that have a real TTS engine plug it in via the {@link LoopbackRealtimeService}
 * constructor. Mirrors C# `LoopbackTextToAudio` delegate.
 */
export type LoopbackTextToAudio = (
  text: string,
  format: RealtimeAudioFormat,
  signal?: AbortSignal,
) => Promise<Uint8Array>;

/**
 * (3.3.0) Default: emit real silence frames sized to ~80ms per word. Real audio
 * bytes (zero amplitude) so downstream signal processing / duration accounting
 * works. Mirrors C# `LoopbackRealtimeService.SilenceTextToAudio`.
 */
export function silenceTextToAudio(text: string, format: RealtimeAudioFormat, _signal?: AbortSignal): Promise<Uint8Array> {
  const sr = sampleRateOf(format);
  const wordCount =
    text == null || text.trim().length === 0
      ? 0
      : text.split(/[ \t\n]+/).filter((w) => w.length > 0).length;
  const durationMs = Math.max(50, wordCount * 80);
  const sampleCount = Math.trunc((sr * durationMs) / 1000);
  const bytes = new Uint8Array(sampleCount * 2); // 16-bit silence (already zeros)
  return Promise.resolve(bytes);
}

/** (3.3.0) In-process loopback {@link IRealtimeService}. Mirrors C# `LoopbackRealtimeService`. */
export class LoopbackRealtimeService implements IRealtimeService {
  private readonly textToAudio: LoopbackTextToAudio;

  constructor(textToAudio: LoopbackTextToAudio = silenceTextToAudio) {
    if (textToAudio == null) throw new Error("textToAudio required");
    this.textToAudio = textToAudio;
  }

  get providerId(): string {
    return "loopback";
  }

  get isConfigured(): boolean {
    return true;
  }

  startSessionAsync(config: RealtimeSessionConfig, _signal?: AbortSignal): Promise<IRealtimeSession> {
    if (config == null) throw new Error("config required");
    return Promise.resolve(new LoopbackRealtimeSession(config, this.textToAudio));
  }
}

/** (3.3.0) A single loopback session. Mirrors C# `LoopbackRealtimeSession`. */
export class LoopbackRealtimeSession implements IRealtimeSession {
  private readonly config: RealtimeSessionConfig;
  private readonly textToAudio: LoopbackTextToAudio;
  private readonly audio = new AsyncQueue<RealtimeAudioFrame>();
  private readonly events = new AsyncQueue<RealtimeEvent>();
  private offsetMs = 0;
  private speaking = false;
  readonly sessionId: string;

  constructor(config: RealtimeSessionConfig, textToAudio: LoopbackTextToAudio = silenceTextToAudio) {
    if (textToAudio == null) throw new Error("textToAudio required");
    this.config = config;
    this.textToAudio = textToAudio;
    this.sessionId = `loop-${uuidN()}`;
  }

  receiveAudioAsync(signal?: AbortSignal): AsyncIterable<RealtimeAudioFrame> {
    return this.audio.drain(signal);
  }

  sendAudioAsync(frame: RealtimeAudioFrame, _signal?: AbortSignal): Promise<void> {
    if (frame == null) throw new Error("frame required");
    const nowSpeaking = !isSilent(frame.pcm);
    if (nowSpeaking !== this.speaking) {
      this.events.enqueue(nowSpeaking ? speechStartedEvent(new Date()) : speechEndedEvent(new Date()));
      this.speaking = nowSpeaking;
    }
    // Loopback: echo received audio back as outbound.
    this.audio.enqueue(frame);
    return Promise.resolve();
  }

  async sendTextAsync(text: string, signal?: AbortSignal): Promise<void> {
    if (text == null) throw new Error("text required");
    this.events.enqueue(transcriptDeltaEvent(new Date(), text, RealtimeDirection.Outbound));
    const pcm = await this.textToAudio(text, this.config.audioFormat, signal);
    if (pcm.length > 0) {
      this.audio.enqueue(realtimeAudioFrame(pcm, this.config.audioFormat, this.offsetMs));
      // Duration of the emitted 16-bit PCM in ms. C# uses double math.
      this.offsetMs += (pcm.length / 2.0 / sampleRateOf(this.config.audioFormat)) * 1000.0;
    }
    this.events.enqueue(transcriptFinalEvent(new Date(), text, RealtimeDirection.Outbound));
    this.events.enqueue(turnCompleteEvent(new Date()));
  }

  sendToolResultAsync(callId: string, resultJson: string, _signal?: AbortSignal): Promise<void> {
    if (callId == null || callId.trim().length === 0) throw new Error("callId required");
    if (resultJson == null) throw new Error("resultJson required");
    this.events.enqueue(
      transcriptDeltaEvent(new Date(), `[tool ${callId}: ${truncate(resultJson, 60)}]`, RealtimeDirection.Outbound),
    );
    return Promise.resolve();
  }

  cancelResponseAsync(_signal?: AbortSignal): Promise<void> {
    this.events.enqueue(turnCompleteEvent(new Date()));
    return Promise.resolve();
  }

  receiveEventsAsync(signal?: AbortSignal): AsyncIterable<RealtimeEvent> {
    return this.events.drain(signal);
  }

  disposeAsync(): Promise<void> {
    this.audio.complete();
    this.events.complete();
    return Promise.resolve();
  }
}

/** RMS-based silence detector over 16-bit linear PCM. Mirrors C# `IsSilent`. */
function isSilent(pcm: Uint8Array): boolean {
  if (pcm.length < 64) return true;
  let sumSq = 0;
  const samples = Math.trunc(pcm.length / 2);
  for (let i = 0; i + 1 < pcm.length; i += 2) {
    // Little-endian int16 (C#: (short)(pcm[i] | (pcm[i+1] << 8))).
    let s = pcm[i] | (pcm[i + 1] << 8);
    if (s >= 0x8000) s -= 0x10000;
    sumSq += s * s;
  }
  const rms = Math.sqrt(sumSq / samples);
  return rms < 250.0; // ~ -42 dBFS
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max) + "…";
}

function uuidN(): string {
  // Compact GUID ("N" format): 32 hex chars, no dashes.
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  // RFC-4122 version/variant bits (matches Guid.NewGuid shape).
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  let out = "";
  for (let i = 0; i < 16; i++) out += bytes[i].toString(16).padStart(2, "0");
  return out;
}
