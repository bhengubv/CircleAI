/**
 * telephony/dtmf_tone_generator
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/dtmf_tone_generator.ts
//
// Generate the dual-tone audio for DTMF digits, plus a helper that sends them
// through any ICallSession via sendAudioAsync — works regardless of whether the
// carrier supports out-of-band DTMF. Faithful port of DtmfToneGenerator.cs.
//
// Float-site note: C# takes `float amplitude`; per the port convention it is
// narrowed with Math.fround at the boundary. The sine synthesis itself is
// `double` in C# and stays double-precision here. PCM-16 samples are written
// little-endian via a DataView, matching `BinaryPrimitives.WriteInt16LittleEndian`.

import type { ICallSession } from "./contracts";
import { audioFrame, CallMediaFormat } from "./primitives";

const INT16_MAX = 32767;

/** Standard DTMF frequencies (low row × high column). */
const FREQUENCIES: ReadonlyMap<string, readonly [low: number, high: number]> = new Map([
  ["1", [697, 1209]],
  ["2", [697, 1336]],
  ["3", [697, 1477]],
  ["A", [697, 1633]],
  ["4", [770, 1209]],
  ["5", [770, 1336]],
  ["6", [770, 1477]],
  ["B", [770, 1633]],
  ["7", [852, 1209]],
  ["8", [852, 1336]],
  ["9", [852, 1477]],
  ["C", [852, 1633]],
  ["*", [941, 1209]],
  ["0", [941, 1336]],
  ["#", [941, 1477]],
  ["D", [941, 1633]],
]);

function clamp(value: number, min: number, max: number): number {
  return value < min ? min : value > max ? max : value;
}

/** Stateless DTMF audio generator. Mirrors static `DtmfToneGenerator`. */
export const DtmfToneGenerator = {
  /**
   * Generate one PCM-16 mono buffer for the digit at the given sample rate.
   * @param digit DTMF digit: 0-9, *, #, A, B, C, D.
   * @param sampleRateHz Output sample rate.
   * @param durationMs Tone duration. Default 150 ms.
   * @param amplitude Peak amplitude 0..1. Default 0.5.
   */
  generate(digit: string, sampleRateHz: number, durationMs = 150, amplitude = 0.5): Uint8Array {
    if (sampleRateHz <= 0) throw new RangeError("sampleRateHz");
    if (durationMs <= 0) throw new RangeError("durationMs");
    const key = (digit ?? "").toUpperCase();
    const pair = FREQUENCIES.get(key);
    if (pair === undefined) {
      throw new Error(`Unsupported DTMF digit '${digit}'.`);
    }
    const amp = Math.fround(amplitude);
    const [low, high] = pair;

    const samples = Math.trunc((sampleRateHz * durationMs) / 1000);
    const buf = new Uint8Array(samples * 2);
    const view = new DataView(buf.buffer);
    for (let i = 0; i < samples; i++) {
      const t = i / sampleRateHz;
      const s = 0.5 * amp * (Math.sin(2 * Math.PI * low * t) + Math.sin(2 * Math.PI * high * t));
      view.setInt16(i * 2, Math.trunc(clamp(s, -1, 1) * INT16_MAX), true);
    }
    return buf;
  },

  /** Generate a full string of digits with gap silence between them. */
  generateSequence(
    digits: string,
    sampleRateHz: number,
    toneDurationMs = 150,
    interDigitGapMs = 50,
    amplitude = 0.5,
  ): Uint8Array {
    if (!digits) return new Uint8Array(0);
    const gapSamples = Math.trunc((sampleRateHz * interDigitGapMs) / 1000);
    const gap = new Uint8Array(gapSamples * 2);

    const parts: Uint8Array[] = [];
    let totalLength = 0;
    for (let i = 0; i < digits.length; i++) {
      const tone = this.generate(digits[i]!, sampleRateHz, toneDurationMs, amplitude);
      parts.push(tone);
      totalLength += tone.length;
      if (i < digits.length - 1) {
        parts.push(gap);
        totalLength += gap.length;
      }
    }
    const out = new Uint8Array(totalLength);
    let offset = 0;
    for (const part of parts) {
      out.set(part, offset);
      offset += part.length;
    }
    return out;
  },

  /** Send `digits` over the call via in-band tones. */
  async sendThroughSessionAsync(
    session: ICallSession,
    digits: string,
    sampleRateHz = 8000,
    toneDurationMs = 150,
    interDigitGapMs = 50,
    signal?: AbortSignal,
  ): Promise<void> {
    if (session === null || session === undefined) throw new Error("session is required");
    if (!digits) return;

    const pcm = this.generateSequence(digits, sampleRateHz, toneDurationMs, interDigitGapMs);
    let format: CallMediaFormat;
    switch (sampleRateHz) {
      case 8000:
        format = CallMediaFormat.Mulaw8000;
        break;
      case 16000:
        format = CallMediaFormat.Pcm16000;
        break;
      case 24000:
        format = CallMediaFormat.Pcm24000;
        break;
      default:
        format = CallMediaFormat.Pcm16000;
        break;
    }
    await session.sendAudioAsync(audioFrame(pcm, format, 0), signal);
  },
} as const;
