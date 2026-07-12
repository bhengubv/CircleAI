// speech/voice_activity_detectors.ts
//
// Frame-at-a-time voice-activity detectors (port of
// CircleAI.Speech.VoiceActivityDetectors.cs):
//   - NullSpeechVoiceActivityDetector: always-speech DI default.
//   - EnergySpeechVoiceActivityDetector: RMS energy + zero-crossing rate +
//     hangover frames. No model needed.
//   - SileroSpeechVoiceActivityDetector: wraps a host IVadModelRunner; falls
//     back to energy scoring until a real model is wired.
//
// FLOAT DISCIPLINE: the C# probability constants + SpeechThreshold are `float`;
// the RMS accumulation is `double`. We fround the float sites so the isSpeech
// threshold comparison matches the C# JIT's single-precision arithmetic.

import { readInt16LE } from "./pcm_io.js";
import { SHORT_MAX } from "./pcm_io.js";
import {
  vadFrameResult,
  type ISpeechVoiceActivityDetector,
  type VadFrameResult,
} from "./contracts.js";

const fr = Math.fround;

/** Always reports speech — DI default so nothing breaks before a real VAD is wired. */
export class NullSpeechVoiceActivityDetector implements ISpeechVoiceActivityDetector {
  static readonly instance = new NullSpeechVoiceActivityDetector();

  get backendId(): string {
    return "null";
  }
  get speechThreshold(): number {
    return fr(0.5);
  }

  classify(_audioPcm16Mono: Uint8Array, _sampleRateHz: number, offsetMs: number): VadFrameResult {
    return vadFrameResult(true, 1, offsetMs);
  }

  reset(): void {
    /* no state */
  }
}

/**
 * Production-grade VAD using RMS energy + zero-crossing rate + hangover-frame
 * smoothing. No ML model required.
 */
export class EnergySpeechVoiceActivityDetector implements ISpeechVoiceActivityDetector {
  private readonly energyThreshold: number;
  private readonly hangoverFrames: number;
  private hangoverRemaining = 0;
  readonly speechThreshold: number;

  constructor(speechThreshold = 0.55, energyThreshold = 0.012, hangoverFrames = 8) {
    this.speechThreshold = fr(speechThreshold);
    this.energyThreshold = fr(energyThreshold);
    this.hangoverFrames = hangoverFrames;
  }

  get backendId(): string {
    return "energy";
  }

  classify(audioPcm16Mono: Uint8Array, _sampleRateHz: number, offsetMs: number): VadFrameResult {
    if (audioPcm16Mono.length < 2) {
      return vadFrameResult(false, 0, offsetMs);
    }

    const sampleCount = Math.trunc(audioPcm16Mono.length / 2);
    let sumSquares = 0; // double
    let zeroCrossings = 0;
    let previous = 0;
    for (let i = 0; i < sampleCount; i++) {
      const s = readInt16LE(audioPcm16Mono, i * 2);
      sumSquares += s * s;
      if (i > 0 && Math.sign(s) !== Math.sign(previous) && s !== 0 && previous !== 0) {
        zeroCrossings++;
      }
      previous = s;
    }
    const rms = Math.sqrt(sumSquares / sampleCount) / SHORT_MAX; // 0..1 (double)
    const zcrRate = fr(zeroCrossings / sampleCount);

    // Speech: high RMS + moderate ZCR (~0.05–0.25 for voiced speech).
    const energyGood = rms >= this.energyThreshold;
    const zcrGood = zcrRate >= fr(0.02) && zcrRate <= fr(0.3);
    let rawProb = fr(energyGood ? (zcrGood ? fr(0.85) : fr(0.6)) : fr(0.1));

    let isSpeech: boolean;
    if (rawProb >= this.speechThreshold) {
      isSpeech = true;
      this.hangoverRemaining = this.hangoverFrames;
    } else if (this.hangoverRemaining > 0) {
      isSpeech = true;
      this.hangoverRemaining--;
      rawProb = Math.max(rawProb, this.speechThreshold);
    } else {
      isSpeech = false;
    }

    return vadFrameResult(isSpeech, rawProb, offsetMs);
  }

  reset(): void {
    this.hangoverRemaining = 0;
  }
}

/** ONNX model runner contract supplied by the host package. Mirrors `IVadModelRunner`. */
export interface IVadModelRunner {
  /** Score one 30 ms / 16 kHz PCM-16 frame; result is 0..1. */
  scoreFrame(audioPcm16Mono: Uint8Array, sampleRateHz: number): number;
}

/**
 * Silero VAD wrapper. Delegates the per-frame score to a host
 * {@link IVadModelRunner}; when no runner is wired it transparently falls back
 * to {@link EnergySpeechVoiceActivityDetector}'s scoring.
 */
export class SileroSpeechVoiceActivityDetector implements ISpeechVoiceActivityDetector {
  private readonly runner: IVadModelRunner | null;
  private readonly fallback: EnergySpeechVoiceActivityDetector;
  private readonly hangoverFrames: number;
  private hangoverRemaining = 0;
  readonly speechThreshold: number;

  constructor(runner: IVadModelRunner | null = null, speechThreshold = 0.5, hangoverFrames = 8) {
    this.runner = runner;
    this.fallback = new EnergySpeechVoiceActivityDetector(speechThreshold);
    this.speechThreshold = fr(speechThreshold);
    this.hangoverFrames = hangoverFrames;
  }

  get backendId(): string {
    return this.runner === null ? "silero (fallback)" : "silero";
  }

  classify(audioPcm16Mono: Uint8Array, sampleRateHz: number, offsetMs: number): VadFrameResult {
    if (this.runner === null) {
      return this.fallback.classify(audioPcm16Mono, sampleRateHz, offsetMs);
    }

    const prob = fr(this.runner.scoreFrame(audioPcm16Mono, sampleRateHz));
    let isSpeech: boolean;
    if (prob >= this.speechThreshold) {
      isSpeech = true;
      this.hangoverRemaining = this.hangoverFrames;
    } else if (this.hangoverRemaining > 0) {
      isSpeech = true;
      this.hangoverRemaining--;
    } else {
      isSpeech = false;
    }
    return vadFrameResult(isSpeech, prob, offsetMs);
  }

  reset(): void {
    this.hangoverRemaining = 0;
    this.fallback.reset();
  }
}
