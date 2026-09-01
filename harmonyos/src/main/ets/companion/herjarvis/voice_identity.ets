/**
 * companion/herjarvis/voice_identity
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/voice_identity.ts
//
// EnergyBandVoiceIdentity — HerJarvisRealImplementations.cs #8. A mean-MFCC
// speaker fingerprint with cosine-similarity matching. Standard pipeline:
// pre-emphasis → 25ms/10ms framing → Hamming window → direct-DFT power spectrum
// → 26-mel filterbank → log → DCT-II → keep 13 cepstral coefficients → mean over
// frames.
//
// FLOAT DISCIPLINE. The C# mixes `float` and `double`:
//   * float sites: PreEmphasis const, PCM decode (s / 32768f), the pre-emphasis
//     filter, the Hamming window (0.54f - 0.46f*(float)cos), and the windowed
//     frame (samples[i] * window[i]).
//   * double sites: the DFT power spectrum, mel energies, logs, DCT, cosine sim.
// We use Float32Array for samples/window/frame and apply Math.fround at every
// float operation so the intermediate values are bit-identical to the C# JIT's
// single-precision arithmetic. Everything from PowerSpectrum onward is double
// (Number), matching the C#.

import { newGuidN as _newGuidN } from "./guid";
import type { IVoiceIdentity } from "./contracts";

// The generated id path is unused here (enrollment is keyed by caller userId),
// but keep the import referenced to avoid an unused-symbol lint if refactored.
void _newGuidN;

const NUM_COEFFICIENTS = 13;
const NUM_MEL_FILTERS = 26;
const FRAME_SIZE = 400; // 25ms @ 16kHz
const FRAME_STEP = 160; // 10ms @ 16kHz
const PRE_EMPHASIS = Math.fround(0.97); // 0.97f

const fr = Math.fround;

/**
 * A mean-MFCC + cosine-similarity speaker identity. `enrollAsync` stores one or
 * more reference fingerprints per user; `identifyAsync` returns the best-matching
 * enrolled user id when cosine similarity exceeds 0.85, else null.
 */
export class EnergyBandVoiceIdentity implements IVoiceIdentity {
  private readonly enrolled = new Map<string, number[][]>();

  async enrollAsync(userId: string, audioPcm16: Uint8Array, sampleRateHz: number): Promise<void> {
    if (!userId || userId.trim().length === 0) throw new Error("userId required");
    const fp = mfcc(audioPcm16, sampleRateHz);
    let list = this.enrolled.get(userId);
    if (list === undefined) {
      list = [];
      this.enrolled.set(userId, list);
    }
    list.push(fp);
  }

  async identifyAsync(audioPcm16: Uint8Array, sampleRateHz: number): Promise<string | null> {
    const fp = mfcc(audioPcm16, sampleRateHz);
    let best: string | null = null;
    let bestSim = -1;
    for (const [userId, refs] of this.enrolled) {
      for (const reference of refs) {
        const sim = cosineSimilarity(fp, reference);
        if (sim > bestSim) {
          bestSim = sim;
          best = userId;
        }
      }
    }
    return bestSim > 0.85 ? best : null;
  }
}

/** Compute the mean MFCC vector (double[NUM_COEFFICIENTS]) across all frames. */
function mfcc(pcm16: Uint8Array, sampleRateHz: number): number[] {
  const samples = decodePcm16(pcm16);
  if (samples.length < FRAME_SIZE) return new Array<number>(NUM_COEFFICIENTS).fill(0);
  preEmphasisFilter(samples);
  const filters = melFilterbank(NUM_MEL_FILTERS, FRAME_SIZE, sampleRateHz);

  const sum = new Array<number>(NUM_COEFFICIENTS).fill(0);
  let count = 0;
  const window = hammingWindow(FRAME_SIZE);
  for (let start = 0; start + FRAME_SIZE <= samples.length; start += FRAME_STEP) {
    const frame = new Float32Array(FRAME_SIZE);
    for (let i = 0; i < FRAME_SIZE; i++) frame[i] = fr(samples[start + i] * window[i]);
    const powerSpec = powerSpectrum(frame);
    const melEnergies = applyFilterbank(powerSpec, filters);
    const logEnergies = new Array<number>(NUM_MEL_FILTERS);
    for (let i = 0; i < NUM_MEL_FILTERS; i++) logEnergies[i] = Math.log(Math.max(1e-10, melEnergies[i]));
    const coeffs = dct(logEnergies, NUM_COEFFICIENTS);
    for (let i = 0; i < NUM_COEFFICIENTS; i++) sum[i] += coeffs[i];
    count++;
  }
  if (count === 0) return sum;
  for (let i = 0; i < NUM_COEFFICIENTS; i++) sum[i] /= count;
  return sum;
}

/** Little-endian PCM16 → float samples in [-1,1) (s / 32768f). */
function decodePcm16(pcm16: Uint8Array): Float32Array {
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

/** In-place pre-emphasis: samples[i] -= 0.97f * samples[i-1], back to front. */
function preEmphasisFilter(samples: Float32Array): void {
  for (let i = samples.length - 1; i > 0; i--) {
    samples[i] = fr(samples[i] - fr(PRE_EMPHASIS * samples[i - 1]));
  }
}

/** Hamming window: w[i] = 0.54f - 0.46f * (float)cos(2πi/(n-1)). */
function hammingWindow(n: number): Float32Array {
  const w = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const cosF = fr(Math.cos((2 * Math.PI * i) / (n - 1)));
    w[i] = fr(fr(0.54) - fr(fr(0.46) * cosF));
  }
  return w;
}

/** Magnitude-squared spectrum via direct DFT (double accumulation). */
function powerSpectrum(frame: Float32Array): number[] {
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

/** numFilters triangular mel filters over the spectrum bins (double). */
function melFilterbank(numFilters: number, frameSize: number, sampleRateHz: number): number[][] {
  const hzToMel = (hz: number) => 2595 * Math.log10(1 + hz / 700.0);
  const melToHz = (mel: number) => 700 * (Math.pow(10, mel / 2595) - 1);
  const lowMel = hzToMel(0);
  const highMel = hzToMel(sampleRateHz / 2.0);
  const melPoints = new Array<number>(numFilters + 2);
  for (let i = 0; i < melPoints.length; i++)
    melPoints[i] = lowMel + ((highMel - lowMel) * i) / (melPoints.length - 1);
  const binPoints = new Array<number>(melPoints.length);
  for (let i = 0; i < melPoints.length; i++)
    binPoints[i] = Math.floor(((frameSize + 1) * melToHz(melPoints[i])) / sampleRateHz);

  const half = Math.trunc(frameSize / 2) + 1;
  const filters: number[][] = [];
  for (let m = 0; m < numFilters; m++) {
    const row = new Array<number>(half).fill(0);
    const left = binPoints[m];
    const centre = binPoints[m + 1];
    const right = binPoints[m + 2];
    for (let k = left; k < centre && k < half; k++)
      if (centre !== left && k >= 0) row[k] = (k - left) / (centre - left);
    for (let k = centre; k < right && k < half; k++)
      if (right !== centre && k >= 0) row[k] = (right - k) / (right - centre);
    filters.push(row);
  }
  return filters;
}

function applyFilterbank(powerSpec: number[], filters: number[][]): number[] {
  const energies = new Array<number>(filters.length);
  for (let m = 0; m < filters.length; m++) {
    let sum = 0;
    const filter = filters[m];
    const len = Math.min(powerSpec.length, filter.length);
    for (let k = 0; k < len; k++) sum += powerSpec[k] * filter[k];
    energies[m] = sum;
  }
  return energies;
}

/** DCT-II of input, keeping the first numCoeffs coefficients (double). */
function dct(input: number[], numCoeffs: number): number[] {
  const n = input.length;
  const output = new Array<number>(numCoeffs);
  for (let k = 0; k < numCoeffs; k++) {
    let sum = 0;
    for (let i = 0; i < n; i++) sum += input[i] * Math.cos((Math.PI * k * (i + 0.5)) / n);
    output[k] = sum;
  }
  return output;
}

function cosineSimilarity(a: number[], b: number[]): number {
  let dot = 0;
  let na = 0;
  let nb = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    na += a[i] * a[i];
    nb += b[i] * b[i];
  }
  return na === 0 || nb === 0 ? 0 : dot / (Math.sqrt(na) * Math.sqrt(nb));
}
