// speech/audio_format_converter.ts
//
// Audio format conversion. Phone carriers feed mu-law / a-law at 8 kHz; cloud
// STT/TTS speak linear PCM at 16/24/44.1 kHz. Port of
// CircleAI.Speech.AudioFormatConverter (+ the AudioCodec enum). All integer
// arithmetic is bit-exact with the C#; there are no float sites.

import { readInt16LE, writeInt16LE } from "./pcm_io.js";

/** Carrier-native audio formats the converter knows how to handle. Mirrors `AudioCodec`. */
export enum AudioCodec {
  /** 16-bit signed linear PCM, little-endian, mono. */
  Pcm16 = "Pcm16",
  /** G.711 μ-law (telephony, North America / Japan). */
  MuLaw = "MuLaw",
  /** G.711 A-law (telephony, Europe). */
  ALaw = "ALaw",
}

/**
 * Convert audio from one (codec, sample rate) to another. Returns a freshly
 * allocated output buffer; the caller does NOT need to size it.
 */
export function convertAudio(
  input: Uint8Array,
  inputCodec: AudioCodec,
  inputSampleRateHz: number,
  outputCodec: AudioCodec,
  outputSampleRateHz: number,
): Uint8Array {
  if (inputSampleRateHz <= 0) throw new RangeError("inputSampleRateHz must be positive.");
  if (outputSampleRateHz <= 0) throw new RangeError("outputSampleRateHz must be positive.");

  // 1) Decode source to PCM-16.
  let pcmIn: Uint8Array;
  switch (inputCodec) {
    case AudioCodec.Pcm16:
      pcmIn = input.slice();
      break;
    case AudioCodec.MuLaw:
      pcmIn = decodeMuLawToPcm16(input);
      break;
    case AudioCodec.ALaw:
      pcmIn = decodeALawToPcm16(input);
      break;
    default:
      throw new Error(`Unknown input codec ${String(inputCodec)}`);
  }

  // 2) Resample if needed.
  const pcmResampled =
    inputSampleRateHz === outputSampleRateHz
      ? pcmIn
      : resamplePcm16Linear(pcmIn, inputSampleRateHz, outputSampleRateHz);

  // 3) Encode to target codec.
  switch (outputCodec) {
    case AudioCodec.Pcm16:
      return pcmResampled;
    case AudioCodec.MuLaw:
      return encodePcm16ToMuLaw(pcmResampled);
    case AudioCodec.ALaw:
      return encodePcm16ToALaw(pcmResampled);
    default:
      throw new Error(`Unknown output codec ${String(outputCodec)}`);
  }
}

// ===== μ-law =====

export function decodeMuLawToPcm16(mulaw: Uint8Array): Uint8Array {
  const pcm = new Uint8Array(mulaw.length * 2);
  for (let i = 0; i < mulaw.length; i++) {
    writeInt16LE(pcm, i * 2, muLawToLinear(mulaw[i]));
  }
  return pcm;
}

export function encodePcm16ToMuLaw(pcm: Uint8Array): Uint8Array {
  const samples = Math.trunc(pcm.length / 2);
  const mulaw = new Uint8Array(samples);
  for (let i = 0; i < samples; i++) {
    mulaw[i] = linearToMuLaw(readInt16LE(pcm, i * 2));
  }
  return mulaw;
}

function muLawToLinear(muByte: number): number {
  // G.711 μ-law decode (ITU-T G.711).
  const mu = ~muByte & 0xff;
  const sign = mu & 0x80;
  const exponent = (mu >> 4) & 0x07;
  const mantissa = mu & 0x0f;
  const magnitude = ((mantissa << 3) + 0x84) << exponent;
  const sample = magnitude - 0x84;
  return (sign !== 0 ? -sample : sample) & 0xffff;
}

function linearToMuLaw(pcm: number): number {
  const Bias = 0x84;
  const Clip = 32635;
  const sign = (pcm >> 8) & 0x80;
  let v = pcm;
  if (sign !== 0) v = -v;
  if (v > Clip) v = Clip;
  v += Bias;

  let exponent: number;
  if (v >= 0x4000) exponent = 7;
  else if (v >= 0x2000) exponent = 6;
  else if (v >= 0x1000) exponent = 5;
  else if (v >= 0x0800) exponent = 4;
  else if (v >= 0x0400) exponent = 3;
  else if (v >= 0x0200) exponent = 2;
  else if (v >= 0x0100) exponent = 1;
  else exponent = 0;

  const mantissa = (v >> (exponent + 3)) & 0x0f;
  return ~(sign | (exponent << 4) | mantissa) & 0xff;
}

// ===== a-law =====

export function decodeALawToPcm16(alaw: Uint8Array): Uint8Array {
  const pcm = new Uint8Array(alaw.length * 2);
  for (let i = 0; i < alaw.length; i++) {
    writeInt16LE(pcm, i * 2, aLawToLinear(alaw[i]));
  }
  return pcm;
}

export function encodePcm16ToALaw(pcm: Uint8Array): Uint8Array {
  const samples = Math.trunc(pcm.length / 2);
  const alaw = new Uint8Array(samples);
  for (let i = 0; i < samples; i++) {
    alaw[i] = linearToALaw(readInt16LE(pcm, i * 2));
  }
  return alaw;
}

function aLawToLinear(aByte: number): number {
  const a = (aByte ^ 0x55) & 0xff;
  const sign = a & 0x80;
  const exponent = (a >> 4) & 0x07;
  const mantissa = a & 0x0f;
  let magnitude: number;
  if (exponent !== 0) {
    magnitude = ((mantissa << 4) + 0x108) << (exponent - 1);
  } else {
    magnitude = (mantissa << 4) + 0x08;
  }
  return (sign !== 0 ? -magnitude : magnitude) & 0xffff;
}

function linearToALaw(pcm: number): number {
  const sign = (pcm >> 8) & 0x80;
  let v = pcm;
  if (sign !== 0) v = -v;
  if (v > 0x7fff) v = 0x7fff;

  let exponent: number;
  let mantissa: number;
  if (v < 256) {
    exponent = 0;
    mantissa = v >> 4;
  } else {
    if (v >= 0x4000) exponent = 7;
    else if (v >= 0x2000) exponent = 6;
    else if (v >= 0x1000) exponent = 5;
    else if (v >= 0x0800) exponent = 4;
    else if (v >= 0x0400) exponent = 3;
    else if (v >= 0x0200) exponent = 2;
    else exponent = 1;
    mantissa = (v >> (exponent + 3)) & 0x0f;
  }
  return ((sign | (exponent << 4) | mantissa) ^ 0x55) & 0xff;
}

// ===== resample (linear interpolation) =====

export function resamplePcm16Linear(pcm: Uint8Array, fromHz: number, toHz: number): Uint8Array {
  if (fromHz === toHz) return pcm;
  const srcSamples = Math.trunc(pcm.length / 2);
  // (int)((long)srcSamples * toHz / fromHz) — integer division truncates.
  const dstSamples = Math.trunc((srcSamples * toHz) / fromHz);
  const dst = new Uint8Array(dstSamples * 2);
  for (let i = 0; i < dstSamples; i++) {
    const srcIdx = (i * fromHz) / toHz;
    const idx0 = Math.floor(srcIdx);
    const idx1 = Math.min(idx0 + 1, srcSamples - 1);
    const frac = srcIdx - idx0;
    const s0 = readInt16LE(pcm, idx0 * 2);
    const s1 = readInt16LE(pcm, idx1 * 2);
    // (short)(s0 + (s1 - s0) * frac) — C# truncates the double toward zero on
    // the (short) cast; Math.trunc reproduces that.
    const s = Math.trunc(s0 + (s1 - s0) * frac);
    writeInt16LE(dst, i * 2, s);
  }
  return dst;
}
