/**
 * voice/dsp
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/dsp.ts
//
// Shared signal-processing helpers used by the energy-based and ONNX-based
// voice components. These are direct ports of the private static helpers that
// appear (duplicated) across EnergyVadDetector.cs, EnergyWakeWordDetector.cs,
// KwsWakeWordDetector.cs, OnnxSpeakerIdentity.cs and voice_identity.ts —
// hoisted here so the port keeps a single copy.
//
// FLOAT DISCIPLINE (mirrors companion/herjarvis/voice_identity.ts):
//   * The C# window/frame/PCM-decode sites are `float`; the DFT power spectrum,
//     mel filterbank, logs and softmax are `double`.
//   * We use Float32Array + Math.fround for the float sites so intermediate
//     values match the C# JIT's single-precision arithmetic, and plain `number`
//     (double) everywhere the C# uses double.

const fr = Math.fround;

/**
 * Decode little-endian signed 16-bit PCM bytes to normalised float samples in
 * [-1, 1) via `s / 32768f`. Trailing odd byte (if any) is ignored, matching the
 * C# `pcm.Length / 2` truncation.
 */
export function decodePcm16ToFloat(pcm16: Uint8Array): Float32Array {
  const n = Math.trunc(pcm16.length / 2);
  const samples = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    // (short)(lo | (hi << 8)) — sign-extend the 16-bit little-endian sample.
    let s = pcm16[i * 2] | (pcm16[i * 2 + 1] << 8);
    if (s >= 0x8000) s -= 0x10000;
    samples[i] = fr(s / 32768);
  }
  return samples;
}

/**
 * Read one little-endian signed 16-bit sample at byte offset `byteOffset`.
 * The analogue of `BinaryPrimitives.ReadInt16LittleEndian`.
 */
export function readInt16LE(pcm16: Uint8Array, byteOffset: number): number {
  let s = pcm16[byteOffset] | (pcm16[byteOffset + 1] << 8);
  if (s >= 0x8000) s -= 0x10000;
  return s;
}

/** Hamming window: `w[i] = 0.54f - 0.46f * (float)cos(2πi/(n-1))`. */
export function hammingWindow(n: number): Float32Array {
  const w = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const cosF = fr(Math.cos((2 * Math.PI * i) / (n - 1)));
    w[i] = fr(fr(0.54) - fr(fr(0.46) * cosF));
  }
  return w;
}

/**
 * Magnitude-squared spectrum via a direct DFT (double accumulation), returning
 * the first `n/2 + 1` bins. Matches the C# `PowerSpectrum` in the voice files.
 */
export function powerSpectrum(frame: Float32Array): number[] {
  const n = frame.length;
  const half = Math.trunc(n / 2) + 1;
  const spec = new Array<number>(half);
  for (let k = 0; k < half; k++) {
    let re = 0;
    let im = 0;
    const omega = (-2.0 * Math.PI * k) / n;
    for (let t = 0; t < n; t++) {
      re += frame[t] * Math.cos(omega * t);
      im += frame[t] * Math.sin(omega * t);
    }
    spec[k] = re * re + im * im;
  }
  return spec;
}

/**
 * `numFilters` triangular mel filters over the spectrum bins (double math).
 * Mirrors the C# `MelFilterbank` used by the KWS / speaker / emotion detectors.
 */
export function melFilterbank(numFilters: number, frameSize: number, sampleRateHz: number): number[][] {
  const hzToMel = (hz: number) => 2595 * Math.log10(1 + hz / 700.0);
  const melToHz = (mel: number) => 700 * (Math.pow(10, mel / 2595) - 1);
  const lowMel = hzToMel(0);
  const highMel = hzToMel(sampleRateHz / 2.0);
  const melPoints = new Array<number>(numFilters + 2);
  for (let i = 0; i < melPoints.length; i++) {
    melPoints[i] = lowMel + ((highMel - lowMel) * i) / (melPoints.length - 1);
  }
  const binPoints = new Array<number>(melPoints.length);
  for (let i = 0; i < melPoints.length; i++) {
    binPoints[i] = Math.floor(((frameSize + 1) * melToHz(melPoints[i])) / sampleRateHz);
  }

  const half = Math.trunc(frameSize / 2) + 1;
  const filters: number[][] = [];
  for (let m = 0; m < numFilters; m++) {
    const row = new Array<number>(half).fill(0);
    const left = binPoints[m];
    const centre = binPoints[m + 1];
    const right = binPoints[m + 2];
    for (let k = left; k < centre && k < half; k++) {
      if (centre !== left) row[k] = (k - left) / (centre - left);
    }
    for (let k = centre; k < right && k < half; k++) {
      if (right !== centre) row[k] = (right - k) / (right - centre);
    }
    filters.push(row);
  }
  return filters;
}

/** RMS energy of a PCM 16-bit frame, normalised to [0, 1] (see EnergyVadDetector). */
export function computeRmsEnergy(frameBytes: Uint8Array): number {
  const sampleCount = Math.trunc(frameBytes.length / 2);
  if (sampleCount === 0) return 0;
  let sumSquares = 0;
  for (let i = 0; i < sampleCount; i++) {
    const s = readInt16LE(frameBytes, i * 2);
    const normalised = s / 32768.0;
    sumSquares += normalised * normalised;
  }
  return fr(Math.sqrt(sumSquares / sampleCount));
}

/** In-place L2 normalisation of an embedding (skips near-zero vectors). */
export function l2Normalise(v: Float32Array): void {
  let sumSq = 0;
  for (let i = 0; i < v.length; i++) sumSq += v[i] * v[i];
  const norm = Math.sqrt(sumSq);
  if (norm < 1e-9) return;
  for (let i = 0; i < v.length; i++) v[i] = fr(v[i] / norm);
}

/**
 * Softmax probability of the `target` class given raw `logits`. Returns 0 when
 * the target index is out of range (matches KwsWakeWordDetector.Softmax).
 */
export function softmaxOf(logits: Float32Array | number[], target: number): number {
  if (target < 0 || target >= logits.length) return 0;
  let max = -Infinity;
  for (let i = 0; i < logits.length; i++) if (logits[i] > max) max = logits[i];
  let denom = 0;
  for (let i = 0; i < logits.length; i++) denom += Math.exp(logits[i] - max);
  const num = Math.exp(logits[target] - max);
  return num / denom;
}

/**
 * Argmax softmax over `logits`, returning the winning `[index, probability]`.
 * Matches OnnxSpeechEmotionDetector.Softmax (returns [-1, 0] for empty input).
 */
export function softmaxArgmax(logits: Float32Array | number[]): [number, number] {
  if (logits.length === 0) return [-1, 0];
  let max = logits[0];
  for (let i = 1; i < logits.length; i++) if (logits[i] > max) max = logits[i];
  let denom = 0;
  for (let i = 0; i < logits.length; i++) denom += Math.exp(logits[i] - max);
  let bestIdx = 0;
  let bestProb = 0;
  for (let i = 0; i < logits.length; i++) {
    const p = Math.exp(logits[i] - max) / denom;
    if (p > bestProb) {
      bestProb = p;
      bestIdx = i;
    }
  }
  return [bestIdx, bestProb];
}
