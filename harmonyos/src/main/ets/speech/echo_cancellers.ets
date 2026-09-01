/**
 * speech/echo_cancellers
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// speech/echo_cancellers.ts
//
// Echo cancellers (port of CircleAI.Speech.EchoCancellers.cs):
//   - NullEchoCanceller: pass-through DI default.
//   - NlmsEchoCanceller: normalised-LMS adaptive filter (pure, no model).
//   - WebRtcEchoCanceller: shell delegating to a host IEchoCancellerModelRunner;
//     falls back to NLMS when no runner is wired.
//
// FLOAT DISCIPLINE: the NLMS filter taps `_w`, the circular reference buffer,
// the sample normalisation (`/ (float)short.MaxValue`), the echo estimate,
// power, error and step size are all C# `float`. We use Float32Array for the
// arrays and Math.fround at every float arithmetic site so the adaptation path
// matches the C# JIT's single-precision behaviour.

import { readInt16LE, writeInt16LE, clampShort, SHORT_MAX } from "./pcm_io";
import type { IEchoCanceller } from "./contracts";

const fr = Math.fround;

/** Pass-through DI default. */
export class NullEchoCanceller implements IEchoCanceller {
  static readonly instance = new NullEchoCanceller();
  get backendId(): string {
    return "null";
  }

  cancel(
    nearEndMicrophone: Uint8Array,
    _farEndReference: Uint8Array,
    _sampleRateHz: number,
    destination: Uint8Array,
  ): number {
    destination.set(nearEndMicrophone.subarray(0, nearEndMicrophone.length), 0);
    return nearEndMicrophone.length;
  }

  reset(): void {
    /* no state */
  }
}

/**
 * Normalised LMS adaptive-filter AEC. Pure, no model downloads. Filter length
 * defaults to 256 taps (~16 ms @ 16 kHz) which covers typical phone-call echo
 * paths.
 */
export class NlmsEchoCanceller implements IEchoCanceller {
  private readonly w: Float32Array;
  private readonly stepSize: number;
  private readonly epsilon: number;
  private readonly filterLength: number;
  private readonly refBuffer: Float32Array;
  private refIndex = 0;

  constructor(filterLength = 256, stepSize = 0.4, epsilon = 1e-6) {
    this.filterLength = filterLength;
    this.stepSize = fr(stepSize);
    this.epsilon = fr(epsilon);
    this.w = new Float32Array(filterLength);
    this.refBuffer = new Float32Array(filterLength);
  }

  get backendId(): string {
    return "nlms";
  }

  cancel(
    nearEndMicrophone: Uint8Array,
    farEndReference: Uint8Array,
    _sampleRateHz: number,
    destination: Uint8Array,
  ): number {
    if (nearEndMicrophone.length !== farEndReference.length) {
      throw new Error("near-end and far-end must be the same length.");
    }
    if (destination.length < nearEndMicrophone.length) {
      throw new Error("destination must be at least as long as input.");
    }

    const sampleCount = Math.trunc(nearEndMicrophone.length / 2);
    for (let n = 0; n < sampleCount; n++) {
      const micSample = fr(readInt16LE(nearEndMicrophone, n * 2) / SHORT_MAX);
      const farSample = fr(readInt16LE(farEndReference, n * 2) / SHORT_MAX);

      // Push far-end into circular reference buffer.
      this.refBuffer[this.refIndex] = farSample;

      // Estimated echo: dot(w, ref).
      let echoEstimate = fr(0);
      let power = this.epsilon;
      for (let k = 0; k < this.filterLength; k++) {
        const rIdx = (this.refIndex - k + this.filterLength) % this.filterLength;
        const x = this.refBuffer[rIdx];
        echoEstimate = fr(echoEstimate + fr(this.w[k] * x));
        power = fr(power + fr(x * x));
      }

      // Error = mic - echo estimate.
      const error = fr(micSample - echoEstimate);

      // Update filter weights.
      const mu = fr(this.stepSize / power);
      for (let k = 0; k < this.filterLength; k++) {
        const rIdx = (this.refIndex - k + this.filterLength) % this.filterLength;
        this.w[k] = fr(this.w[k] + fr(fr(mu * error) * this.refBuffer[rIdx]));
      }

      this.refIndex = (this.refIndex + 1) % this.filterLength;

      // Clamp + write.
      const outSample = Math.trunc(clampShort(fr(error * SHORT_MAX)));
      writeInt16LE(destination, n * 2, outSample);
    }

    return nearEndMicrophone.length;
  }

  reset(): void {
    this.w.fill(0);
    this.refBuffer.fill(0);
    this.refIndex = 0;
  }
}

/** Host-supplied AEC model runner (e.g. WebRTC AEC3). Mirrors `IEchoCancellerModelRunner`. */
export interface IEchoCancellerModelRunner {
  process(
    nearEnd: Uint8Array,
    farEnd: Uint8Array,
    sampleRateHz: number,
    destination: Uint8Array,
  ): number;
  reset(): void;
}

/** WebRTC AEC3 wrapper — falls back to NLMS when no runner is wired. */
export class WebRtcEchoCanceller implements IEchoCanceller {
  private readonly runner: IEchoCancellerModelRunner | null;
  private readonly fallback = new NlmsEchoCanceller();

  constructor(runner: IEchoCancellerModelRunner | null = null) {
    this.runner = runner;
  }

  get backendId(): string {
    return this.runner === null ? "webrtc-aec3 (fallback)" : "webrtc-aec3";
  }

  cancel(
    nearEndMicrophone: Uint8Array,
    farEndReference: Uint8Array,
    sampleRateHz: number,
    destination: Uint8Array,
  ): number {
    return this.runner === null
      ? this.fallback.cancel(nearEndMicrophone, farEndReference, sampleRateHz, destination)
      : this.runner.process(nearEndMicrophone, farEndReference, sampleRateHz, destination);
  }

  reset(): void {
    this.fallback.reset();
    this.runner?.reset();
  }
}
