// wav_io.ts
//
// Port of src/CircleAI.Voice/WavIo.cs — minimal RIFF/WAVE reading and PCM-16
// packing, so a reference recording can become the float samples a voice needs.
//
// Parity is asserted against fixtures/voice_wav_io.json.

/** Mimi's sample rate — what `toMono24k` resamples to. */
export const VOICE_TARGET_RATE = 24000;

export interface Wav {
  /** Interleaved float samples in [-1,1]. */
  readonly samples: Float32Array;
  readonly rate: number;
  readonly channels: number;
}

/** Parse a RIFF/WAVE buffer. */
export function parseWav(raw: Uint8Array): Wav {
  const view = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
  if (
    raw.length < 12 ||
    view.getUint32(0, false) !== 0x52494646 || // "RIFF"
    view.getUint32(8, false) !== 0x57415645    // "WAVE"
  ) {
    throw new Error('not a RIFF/WAVE file');
  }

  let format = 0;
  let channels = 0;
  let rate = 0;
  let bits = 0;
  let dataStart = -1;
  let dataSize = 0;
  let offset = 12;

  // WALK THE CHUNKS. A WAV written by anything other than the simplest encoder
  // carries LIST/fact/cue chunks before the data, and assuming data starts at
  // byte 44 reads metadata as audio — which sounds like a short burst of noise
  // before the real recording.
  while (offset + 8 <= raw.length) {
    const id = view.getUint32(offset, false);
    let size = view.getInt32(offset + 4, true);
    const body = offset + 8;
    if (size < 0 || body + size > raw.length) size = raw.length - body;

    if (id === 0x666d7420) {
      // "fmt "
      format = view.getUint16(body, true);
      channels = view.getUint16(body + 2, true);
      rate = view.getInt32(body + 4, true);
      bits = view.getUint16(body + 14, true);
    } else if (id === 0x64617461) {
      // "data"
      dataStart = body;
      dataSize = size;
    }

    offset = body + size + (size & 1); // chunks are word-aligned
  }

  if (channels === 0 || rate === 0 || dataStart < 0 || dataSize === 0) {
    throw new Error('no usable fmt/data chunk');
  }

  // 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format lives
  // in a sub-chunk — treated as PCM here, which is what it is in every file the
  // voice stack has met.
  const pcm = format === 1 || format === 0xfffe;
  const at = (i: number) => dataStart + i;
  let samples: Float32Array;

  if (pcm && bits === 8) {
    samples = mapSamples(dataSize, 1, (i) => (raw[at(i)] - 128) / 128);
  } else if (pcm && bits === 16) {
    samples = mapSamples(dataSize, 2, (i) => view.getInt16(at(i), true) / 32768);
  } else if (pcm && bits === 24) {
    samples = mapSamples(dataSize, 3, (i) => {
      const v = raw[at(i)] | (raw[at(i + 1)] << 8) | (raw[at(i + 2)] << 16);
      return ((v << 8) >> 8) / 8388608;
    });
  } else if (pcm && bits === 32) {
    samples = mapSamples(dataSize, 4, (i) => view.getInt32(at(i), true) / 2147483648);
  } else if (format === 3 && bits === 32) {
    samples = mapSamples(dataSize, 4, (i) => view.getFloat32(at(i), true));
  } else {
    throw new Error(`WAV format ${format} at ${bits} bits is not decoded by this reader`);
  }

  return { samples, rate, channels };
}

/** Downmix to mono, resample to 24 kHz, and cap the length. */
export function toMono24k(wav: Wav, maxSeconds = 30): Float32Array {
  let samples = wav.samples;

  if (wav.channels > 1) {
    const mono = new Float32Array(Math.floor(samples.length / wav.channels));
    for (let i = 0; i < mono.length; i++) {
      let sum = 0;
      for (let c = 0; c < wav.channels; c++) sum += samples[i * wav.channels + c];
      mono[i] = Math.fround(sum / wav.channels);
    }
    samples = mono;
  }

  if (wav.rate !== VOICE_TARGET_RATE) samples = resample(samples, wav.rate, VOICE_TARGET_RATE);

  const cap = maxSeconds * VOICE_TARGET_RATE;
  return samples.length > cap ? samples.slice(0, cap) : samples;
}

/** Pack float samples in [-1,1] as little-endian signed 16-bit PCM. */
export function toPcm16(samples: ArrayLike<number>): Uint8Array {
  const out = new Uint8Array(samples.length * 2);
  const view = new DataView(out.buffer);
  for (let i = 0; i < samples.length; i++) {
    const clamped = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(i * 2, Math.trunc(clamped * 32767), true);
  }
  return out;
}

function mapSamples(
  byteCount: number,
  stride: number,
  convert: (byteOffset: number) => number,
): Float32Array {
  const count = Math.floor(byteCount / stride);
  const out = new Float32Array(count);
  for (let i = 0; i < count; i++) out[i] = Math.fround(convert(i * stride));
  return out;
}

/** Linear resample. Adequate here: the target is a speaker embedding, not playback. */
function resample(input: Float32Array, from: number, to: number): Float32Array {
  if (input.length === 0) return input;
  const count = Math.max(Math.round((input.length * to) / from), 1);
  const out = new Float32Array(count);
  const step = (input.length - 1) / Math.max(count - 1, 1);
  for (let i = 0; i < count; i++) {
    const x = i * step;
    const lo = Math.floor(x);
    const hi = Math.min(lo + 1, input.length - 1);
    out[i] = Math.fround(input[lo] + (input[hi] - input[lo]) * (x - lo));
  }
  return out;
}
