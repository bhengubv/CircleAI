/**
 * speech/noise_reducers
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// speech/noise_reducers.ts
//
// Noise reducers (port of CircleAI.Speech.NoiseReducers.cs):
//   - NullNoiseReducer: no-op pass-through, BackendId="null".
//   - SpectralSubtractionNoiseReducer: lightweight time-domain noise gate
//     (envelope-following, no model).
//   - KrispNoiseReducer / DeepFilterNetNoiseReducer: thin shells delegating to
//     a host INoiseReducerModelRunner; fall back to spectral subtraction.
//
// FLOAT DISCIPLINE: `_floorEstimate` and `_attenuation` are C# `float`; the
// noise-floor threshold and the attenuated sample are computed in single
// precision. We fround those sites.

import { readInt16LE, writeInt16LE, SHORT_MAX } from "./pcm_io";
import type { INoiseReducer } from "./contracts";

const fr = Math.fround;

/** No-op reducer — DI default. */
export class NullNoiseReducer implements INoiseReducer {
  static readonly instance = new NullNoiseReducer();
  get backendId(): string {
    return "null";
  }
  get isAvailable(): boolean {
    return true;
  }

  reduce(audioPcm16Mono: Uint8Array, _sampleRateHz: number, destination: Uint8Array): number {
    destination.set(audioPcm16Mono.subarray(0, audioPcm16Mono.length), 0);
    return audioPcm16Mono.length;
  }
}

/**
 * Lightweight time-domain noise gate: attenuates samples whose magnitude sits
 * below an estimated noise floor with a soft knee. Not as clean as a DNN but
 * adds zero runtime cost and works on every device.
 */
export class SpectralSubtractionNoiseReducer implements INoiseReducer {
  private readonly floorEstimate: number;
  private readonly attenuation: number;

  constructor(floorEstimate = 0.008, attenuation = 0.25) {
    this.floorEstimate = fr(floorEstimate);
    this.attenuation = fr(attenuation);
  }

  get backendId(): string {
    return "passthrough";
  }
  get isAvailable(): boolean {
    return true;
  }

  reduce(audioPcm16Mono: Uint8Array, _sampleRateHz: number, destination: Uint8Array): number {
    if (destination.length < audioPcm16Mono.length) {
      throw new Error("destination must be at least as long as input.");
    }

    const sampleCount = Math.trunc(audioPcm16Mono.length / 2);
    const floor = Math.trunc(fr(this.floorEstimate * SHORT_MAX));
    for (let i = 0; i < sampleCount; i++) {
      const s = readInt16LE(audioPcm16Mono, i * 2);
      const abs = Math.abs(s);
      if (abs <= floor) {
        writeInt16LE(destination, i * 2, Math.trunc(fr(s * this.attenuation)));
      } else {
        writeInt16LE(destination, i * 2, s);
      }
    }
    return audioPcm16Mono.length;
  }
}

/** Host-supplied DNN runner for noise reduction. Mirrors `INoiseReducerModelRunner`. */
export interface INoiseReducerModelRunner {
  /** Process one frame; write cleaned PCM-16 mono into the destination buffer. */
  process(audioPcm16Mono: Uint8Array, sampleRateHz: number, destination: Uint8Array): number;
}

/** Krisp wrapper — uses the host's INoiseReducerModelRunner when present. */
export class KrispNoiseReducer implements INoiseReducer {
  private readonly runner: INoiseReducerModelRunner | null;
  private readonly fallback = new SpectralSubtractionNoiseReducer();

  constructor(runner: INoiseReducerModelRunner | null = null) {
    this.runner = runner;
  }

  get backendId(): string {
    return this.runner === null ? "krisp (fallback)" : "krisp";
  }
  get isAvailable(): boolean {
    return true;
  }

  reduce(audioPcm16Mono: Uint8Array, sampleRateHz: number, destination: Uint8Array): number {
    return this.runner === null
      ? this.fallback.reduce(audioPcm16Mono, sampleRateHz, destination)
      : this.runner.process(audioPcm16Mono, sampleRateHz, destination);
  }
}

/** DeepFilterNet wrapper — falls back to spectral subtraction when no runner is wired. */
export class DeepFilterNetNoiseReducer implements INoiseReducer {
  private readonly runner: INoiseReducerModelRunner | null;
  private readonly fallback = new SpectralSubtractionNoiseReducer();

  constructor(runner: INoiseReducerModelRunner | null = null) {
    this.runner = runner;
  }

  get backendId(): string {
    return this.runner === null ? "deepfilternet (fallback)" : "deepfilternet";
  }
  get isAvailable(): boolean {
    return true;
  }

  reduce(audioPcm16Mono: Uint8Array, sampleRateHz: number, destination: Uint8Array): number {
    return this.runner === null
      ? this.fallback.reduce(audioPcm16Mono, sampleRateHz, destination)
      : this.runner.process(audioPcm16Mono, sampleRateHz, destination);
  }
}
