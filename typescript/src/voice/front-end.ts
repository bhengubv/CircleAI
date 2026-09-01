// The voice front end: features, phonemes, wake words, and the loop.
//
// THIS IS THE PART THAT MUST MATCH A MODEL EXACTLY. Everything else in the port
// can be a reasonable interpretation; here, a filterbank that is a fraction off
// what a model was trained on does not fail - it transcribes confident
// nonsense, and the blame lands on the model.
//
// THE FOUR THAT ARE ALWAYS WRONG, verified against the C# fixtures rather than
// reasoned about:
//
//   * Kaldi's default window is POVEY, not Hann: (0.5 - 0.5cos)^0.85. The
//     exponent is the whole difference and it is easy to miss in a formula.
//
//   * `highFreq = -400` does NOT mean 400 Hz. A negative value is an OFFSET
//     FROM NYQUIST, so at 16 kHz it means 7600. Reading it as a frequency puts
//     every mel bin in the wrong place.
//
//   * `snipEdges = false` CENTRES the frames and MIRRORS at the boundaries.
//     Zero-padding instead is the obvious implementation and it puts a
//     brightness ramp on the first and last frames of every utterance.
//
//   * The log floor is float32 epsilon, 1.19e-7 - not 1e-10, and not
//     `Number.EPSILON`, which is the float64 one and about a thousand times
//     smaller. A different floor changes every silent frame's value.

import { isEthiopic, romanize as romanizeGeez } from "./geez_romanizer";

// ─────────────────────────────────────────────────────────────────────────────
// Audio in

/** The shape of a block of PCM. */
export interface AudioPcmFormat {
  readonly sampleRateHz: number;
  readonly channels: number;
  readonly bitsPerSample: number;
}

export const audioPcmFormat = (
  sampleRateHz = 16000,
  channels = 1,
  bitsPerSample = 16,
): AudioPcmFormat => {
  if (sampleRateHz <= 0 || channels <= 0) {
    throw new Error("a PCM format needs a positive rate and channel count");
  }
  return Object.freeze({ sampleRateHz, channels, bitsPerSample });
};

/**
 * 16 kHz mono is what every speech model here wants.
 *
 * Named rather than repeated, because a transcriber fed 22050 does not fail -
 * it hears the wrong speed and transcribes it confidently.
 */
export const SPEECH_FORMAT = audioPcmFormat(16000, 1, 16);

/**
 * WAV in both directions.
 *
 * CHUNKS ARE WALKED, not assumed. A WAV from a recorder usually has a LIST or
 * fact chunk between `fmt ` and `data`, and code that seeks to a fixed offset
 * reads that metadata as audio - which plays as a burst of noise at the start
 * of every file from that recorder.
 */
export class WavIo {
  /**
   * THE TWO SIZE FIELDS ARE DIFFERENT: the RIFF size is the whole file minus 8,
   * and the data size is the PCM bytes only. Getting either wrong produces a
   * file that plays in one program and not another - the worst kind of wrong,
   * because the first program you test in is usually the forgiving one.
   *
   * Everything in a WAV header is LITTLE-endian, unlike PNG.
   */
  static header(format: AudioPcmFormat, dataBytes: number): Uint8Array {
    const out = new Uint8Array(44);
    const view = new DataView(out.buffer);
    const ascii = (at: number, s: string) => {
      for (let i = 0; i < s.length; i++) out[at + i] = s.charCodeAt(i);
    };
    const blockAlign = (format.channels * format.bitsPerSample) / 8;
    ascii(0, "RIFF");
    view.setUint32(4, 36 + dataBytes, true);
    ascii(8, "WAVEfmt ");
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true); // PCM, uncompressed
    view.setUint16(22, format.channels, true);
    view.setUint32(24, format.sampleRateHz, true);
    view.setUint32(28, format.sampleRateHz * blockAlign, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, format.bitsPerSample, true);
    ascii(36, "data");
    view.setUint32(40, dataBytes, true);
    return out;
  }

  /**
   * Floats in -1..1 to 16-bit.
   *
   * CLAMPED, not wrapped. A sample of 1.2 that wraps becomes a large negative
   * number - a click at full scale, louder than anything else in the file.
   * Scaled by 32767 rather than 32768 so +1.0 is representable and does not
   * become the one value that wraps.
   */
  static write(format: AudioPcmFormat, samples: readonly number[]): Uint8Array {
    const body = new Uint8Array(samples.length * 2);
    const view = new DataView(body.buffer);
    for (let i = 0; i < samples.length; i++) {
      const s = samples[i] < -1 ? -1 : samples[i] > 1 ? 1 : samples[i];
      view.setInt16(i * 2, Math.round(s * 32767), true);
    }
    const head = WavIo.header(format, body.length);
    const out = new Uint8Array(head.length + body.length);
    out.set(head, 0);
    out.set(body, head.length);
    return out;
  }

  static read(data: Uint8Array): { format: AudioPcmFormat; samples: number[] } {
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const tag = (at: number) =>
      String.fromCharCode(data[at], data[at + 1], data[at + 2], data[at + 3]);
    if (data.length < 12 || tag(0) !== "RIFF" || tag(8) !== "WAVE") {
      throw new Error("not a RIFF/WAVE file");
    }
    let p = 12;
    let format: AudioPcmFormat | undefined;
    let samples: number[] = [];
    while (p + 8 <= data.length) {
      const kind = tag(p);
      const size = view.getUint32(p + 4, true);
      if (kind === "fmt " && size >= 16) {
        format = audioPcmFormat(
          view.getUint32(p + 12, true),
          view.getUint16(p + 10, true),
          view.getUint16(p + 22, true),
        );
      } else if (kind === "data") {
        const count = Math.floor(Math.min(size, data.length - p - 8) / 2);
        samples = new Array(count);
        for (let i = 0; i < count; i++) samples[i] = view.getInt16(p + 8 + i * 2, true) / 32768;
      }
      // Chunks are WORD-ALIGNED: an odd-sized chunk is followed by a pad byte
      // that is not counted in its size. Skipping it puts every subsequent
      // chunk one byte out.
      p += 8 + size + (size & 1);
    }
    if (!format) throw new Error("this WAV has no fmt chunk");
    return { format, samples };
  }

  /**
   * Linear resampling, and it is honest about being that.
   *
   * Good enough to feed a wake detector and NOT good enough to feed a
   * transcriber trained on properly filtered audio - downsampling without a
   * low-pass folds everything above the new Nyquist back into the band, which a
   * model hears as noise it was never trained on.
   */
  static resampleLinear(samples: readonly number[], fromHz: number, toHz: number): number[] {
    if (fromHz === toHz || samples.length === 0) return [...samples];
    const ratio = fromHz / toHz;
    const count = Math.max(1, Math.floor(samples.length / ratio));
    const out = new Array<number>(count);
    for (let i = 0; i < count; i++) {
      const position = i * ratio;
      const left = Math.floor(position);
      const frac = position - left;
      const right = Math.min(left + 1, samples.length - 1);
      out[i] = samples[left] * (1 - frac) + samples[right] * frac;
    }
    return out;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Features

/** How the filterbank is configured. Kaldi's defaults, named. */
export interface KaldiFbankOptions {
  readonly sampleRateHz: number;
  readonly frameLengthMs: number;
  readonly frameShiftMs: number;
  readonly melBins: number;
  readonly lowFreq: number;
  /**
   * NEGATIVE MEANS AN OFFSET FROM NYQUIST. -400 at 16 kHz is 7600 Hz, not 400.
   * Reading it as a frequency puts every mel bin in the wrong place, and the
   * model hears a voice with no top end.
   */
  readonly highFreq: number;
  readonly preEmphasis: number;
  readonly dither: number;
  /**
   * False CENTRES the frames and MIRRORS at the boundaries. Zero-padding
   * instead is the obvious implementation and puts a brightness ramp on the
   * first and last frames of every utterance.
   */
  readonly snipEdges: boolean;
  readonly removeDcOffset: boolean;
}

export const kaldiFbankOptions = (
  partial: Partial<KaldiFbankOptions> = {},
): KaldiFbankOptions =>
  Object.freeze({
    sampleRateHz: partial.sampleRateHz ?? 16000,
    frameLengthMs: partial.frameLengthMs ?? 25,
    frameShiftMs: partial.frameShiftMs ?? 10,
    melBins: partial.melBins ?? 80,
    lowFreq: partial.lowFreq ?? 20,
    highFreq: partial.highFreq ?? -400,
    preEmphasis: partial.preEmphasis ?? 0.97,
    dither: partial.dither ?? 0,
    snipEdges: partial.snipEdges ?? false,
    removeDcOffset: partial.removeDcOffset ?? true,
  });

/**
 * Kaldi-compatible log-mel filterbank features.
 *
 * "Compatible" is the whole requirement. A model trained on Kaldi features and
 * fed something almost-Kaldi does not error; it degrades, and the degradation
 * looks like a bad microphone.
 */
export class KaldiFbank {
  /** float32 epsilon - NOT Number.EPSILON, which is the float64 one and about
   * a thousand times smaller. The floor changes every silent frame's value. */
  static readonly LOG_FLOOR = 1.1920928955078125e-7;

  constructor(readonly options: KaldiFbankOptions = kaldiFbankOptions()) {}

  get frameLength(): number {
    return Math.round((this.options.sampleRateHz * this.options.frameLengthMs) / 1000);
  }

  get frameShift(): number {
    return Math.round((this.options.sampleRateHz * this.options.frameShiftMs) / 1000);
  }

  /** The FFT size is the next power of two at or above the frame length. */
  get fftSize(): number {
    let n = 1;
    while (n < this.frameLength) n <<= 1;
    return n;
  }

  /** The resolved upper edge, with the negative-means-offset rule applied. */
  get highFrequency(): number {
    const nyquist = this.options.sampleRateHz / 2;
    return this.options.highFreq <= 0 ? nyquist + this.options.highFreq : this.options.highFreq;
  }

  /**
   * The POVEY window: (0.5 - 0.5cos)^0.85.
   *
   * The 0.85 exponent is Kaldi's default and is the entire difference from a
   * Hann window. Missing it is subtle enough to survive a review and large
   * enough to move every feature value.
   */
  static poveyWindow(length: number): Float64Array {
    const w = new Float64Array(length);
    for (let i = 0; i < length; i++) {
      w[i] = Math.pow(0.5 - 0.5 * Math.cos((2 * Math.PI * i) / (length - 1)), 0.85);
    }
    return w;
  }

  static melOf(hz: number): number {
    return 1127 * Math.log(1 + hz / 700);
  }

  static hzOf(mel: number): number {
    return 700 * (Math.exp(mel / 1127) - 1);
  }

  /**
   * The triangular mel filters, as (start bin, weights).
   *
   * Kaldi's filters are built on the MEL scale with equal spacing there, and
   * each triangle spans from the previous centre to the next - so neighbouring
   * filters overlap by half. Non-overlapping filters are a common
   * simplification and they lose energy between bins.
   */
  melBanks(): { start: number; weights: Float64Array }[] {
    const { melBins, lowFreq, sampleRateHz } = this.options;
    const fftSize = this.fftSize;
    const bins = fftSize / 2 + 1;
    const binHz = sampleRateHz / fftSize;
    const lowMel = KaldiFbank.melOf(lowFreq);
    const highMel = KaldiFbank.melOf(this.highFrequency);
    const step = (highMel - lowMel) / (melBins + 1);

    const out: { start: number; weights: Float64Array }[] = [];
    for (let m = 0; m < melBins; m++) {
      const left = KaldiFbank.hzOf(lowMel + m * step);
      const centre = KaldiFbank.hzOf(lowMel + (m + 1) * step);
      const right = KaldiFbank.hzOf(lowMel + (m + 2) * step);
      const weights: number[] = [];
      let start = -1;
      for (let b = 0; b < bins; b++) {
        const hz = b * binHz;
        if (hz <= left || hz >= right) continue;
        if (start < 0) start = b;
        weights.push(hz <= centre ? (hz - left) / (centre - left) : (right - hz) / (right - centre));
      }
      out.push({ start: Math.max(0, start), weights: Float64Array.from(weights) });
    }
    return out;
  }

  /**
   * Splits into frames, mirroring at the edges when `snipEdges` is false.
   *
   * MIRRORING, not zero-padding. A zero-padded first frame has a sharp step at
   * its start, which is broadband energy the model reads as a consonant.
   */
  frames(samples: readonly number[]): Float64Array[] {
    const length = this.frameLength;
    const shift = this.frameShift;
    if (samples.length === 0) return [];

    const read = (i: number): number => {
      if (i >= 0 && i < samples.length) return samples[i];
      if (!this.options.snipEdges) {
        // Reflect. `-1` maps to sample 1, not sample 0, so the boundary sample
        // is not duplicated - duplicating it flattens the first derivative and
        // shows up as a click in the reconstructed signal.
        const n = samples.length;
        if (n === 1) return samples[0];
        let j = i < 0 ? -i : 2 * (n - 1) - i;
        while (j < 0 || j >= n) j = j < 0 ? -j : 2 * (n - 1) - j;
        return samples[j];
      }
      return 0;
    };

    const count = this.options.snipEdges
      ? Math.max(0, Math.floor((samples.length - length) / shift) + 1)
      : Math.max(1, Math.round(samples.length / shift));
    const offset = this.options.snipEdges ? 0 : -Math.floor(length / 2);

    const out: Float64Array[] = [];
    for (let f = 0; f < count; f++) {
      const frame = new Float64Array(length);
      for (let i = 0; i < length; i++) frame[i] = read(f * shift + offset + i);
      out.push(frame);
    }
    return out;
  }

  /**
   * One frame's log-mel energies.
   *
   * The order is fixed and each step depends on the last: DC removal, then
   * pre-emphasis, then the window. Pre-emphasising before removing DC amplifies
   * an offset into the first sample; windowing before pre-emphasis applies the
   * filter across the taper.
   */
  frameFeatures(frame: Float64Array, window: Float64Array, banks: ReturnType<KaldiFbank["melBanks"]>): Float64Array {
    const work = Float64Array.from(frame);
    if (this.options.removeDcOffset) {
      let mean = 0;
      for (const v of work) mean += v;
      mean /= work.length;
      for (let i = 0; i < work.length; i++) work[i] -= mean;
    }
    if (this.options.preEmphasis > 0) {
      // BACKWARDS, so each sample sees the ORIGINAL previous one. Forwards, the
      // second sample is filtered against an already-filtered first.
      for (let i = work.length - 1; i > 0; i--) {
        work[i] -= this.options.preEmphasis * work[i - 1];
      }
      work[0] -= this.options.preEmphasis * work[0];
    }
    for (let i = 0; i < work.length; i++) work[i] *= window[i];

    const power = KaldiFbank.powerSpectrum(work, this.fftSize);
    const out = new Float64Array(banks.length);
    for (let m = 0; m < banks.length; m++) {
      const { start, weights } = banks[m];
      let sum = 0;
      for (let i = 0; i < weights.length; i++) sum += power[start + i] * weights[i];
      out[m] = Math.log(Math.max(sum, KaldiFbank.LOG_FLOOR));
    }
    return out;
  }

  /**
   * The power spectrum, by a radix-2 FFT.
   *
   * Real input, so only the first N/2+1 bins are kept - the rest are the
   * conjugate mirror and carry no information. Keeping them all would double
   * the energy in every mel bin that spans the midpoint.
   */
  static powerSpectrum(frame: Float64Array, fftSize: number): Float64Array {
    const re = new Float64Array(fftSize);
    const im = new Float64Array(fftSize);
    re.set(frame.subarray(0, Math.min(frame.length, fftSize)));

    // Bit-reversal permutation.
    for (let i = 1, j = 0; i < fftSize; i++) {
      let bit = fftSize >> 1;
      for (; j & bit; bit >>= 1) j ^= bit;
      j ^= bit;
      if (i < j) {
        [re[i], re[j]] = [re[j], re[i]];
        [im[i], im[j]] = [im[j], im[i]];
      }
    }
    for (let len = 2; len <= fftSize; len <<= 1) {
      const angle = (-2 * Math.PI) / len;
      const wr = Math.cos(angle);
      const wi = Math.sin(angle);
      for (let i = 0; i < fftSize; i += len) {
        let cr = 1;
        let ci = 0;
        for (let k = 0; k < len / 2; k++) {
          const ur = re[i + k];
          const ui = im[i + k];
          const vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
          const vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
          re[i + k] = ur + vr;
          im[i + k] = ui + vi;
          re[i + k + len / 2] = ur - vr;
          im[i + k + len / 2] = ui - vi;
          const nr = cr * wr - ci * wi;
          ci = cr * wi + ci * wr;
          cr = nr;
        }
      }
    }
    const bins = fftSize / 2 + 1;
    const out = new Float64Array(bins);
    for (let b = 0; b < bins; b++) out[b] = re[b] * re[b] + im[b] * im[b];
    return out;
  }

  compute(samples: readonly number[]): Float64Array[] {
    const window = KaldiFbank.poveyWindow(this.frameLength);
    const banks = this.melBanks();
    return this.frames(samples).map((f) => this.frameFeatures(f, window, banks));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Text in

/**
 * Splits text into sentences.
 *
 * ABBREVIATIONS ARE THE WHOLE PROBLEM. "Dr. Nkosi arrived." is one sentence and
 * a splitter that breaks on every full stop makes it two - which a text-to-
 * speech engine then reads with a pause in the wrong place, every time.
 */
export class SentenceSplitter {
  /** Common abbreviations that end in a full stop and do not end a sentence. */
  private static readonly abbreviations = new Set([
    "mr", "mrs", "ms", "dr", "prof", "rev", "hon", "st", "jr", "sr",
    "vs", "etc", "eg", "ie", "no", "fig", "approx", "dept", "univ",
  ]);

  static split(text: string): string[] {
    const out: string[] = [];
    let current = "";
    const chars = [...text];
    for (let i = 0; i < chars.length; i++) {
      const c = chars[i];
      current += c;
      if (c !== "." && c !== "!" && c !== "?" && c !== "。") continue;

      // A run of terminators is ONE break - "What?!" is one sentence.
      while (i + 1 < chars.length && ".!?".includes(chars[i + 1])) {
        current += chars[++i];
      }
      const next = chars[i + 1];
      // No break without whitespace after it: "3.14" and "example.com" are not
      // two sentences.
      if (next !== undefined && !/\s/.test(next)) continue;

      const lastWord = /([A-Za-z]+)\.$/.exec(current.trimEnd());
      if (lastWord && SentenceSplitter.abbreviations.has(lastWord[1].toLowerCase())) continue;

      // A single capital before the stop is an initial - "J. M. Coetzee".
      if (/(^|\s)[A-Z]\.$/.test(current.trimEnd())) continue;

      out.push(current.trim());
      current = "";
    }
    if (current.trim()) out.push(current.trim());
    return out;
  }
}

/** One run of text in a single script. */
export interface LanguageSpan {
  readonly text: string;
  readonly script: string;
  readonly start: number;
}

/**
 * Splits text by SCRIPT, so each run can go to the right voice.
 *
 * A sentence mixing Latin and Ge'ez read entirely by a Latin voice is
 * unintelligible for half its length, and this is the common case in the
 * languages this is for - a loanword or a name in the middle of a sentence.
 */
export class LanguageSpanSplitter {
  static scriptOf(ch: string): string {
    const c = ch.codePointAt(0) ?? 0;
    if (c >= 0x1200 && c <= 0x137f) return "Ethiopic";
    if (c >= 0x0600 && c <= 0x06ff) return "Arabic";
    if (c >= 0x0400 && c <= 0x04ff) return "Cyrillic";
    if (c >= 0x0900 && c <= 0x097f) return "Devanagari";
    if (c >= 0x4e00 && c <= 0x9fff) return "Han";
    if (c >= 0x3040 && c <= 0x30ff) return "Kana";
    if (c >= 0xac00 && c <= 0xd7af) return "Hangul";
    if (/[A-Za-zÀ-ɏ]/.test(ch)) return "Latin";
    return "Common";
  }

  static split(text: string): LanguageSpan[] {
    const out: LanguageSpan[] = [];
    let current = "";
    let script = "";
    let start = 0;
    let index = 0;
    for (const ch of text) {
      const s = LanguageSpanSplitter.scriptOf(ch);
      // "Common" - spaces and punctuation - JOINS the run it is in rather than
      // starting a new one. Splitting on every space would produce a span per
      // word and lose the point of spans entirely.
      if (s !== "Common" && script && s !== script) {
        out.push(Object.freeze({ text: current, script, start }));
        current = "";
        start = index;
      }
      if (s !== "Common") script = s;
      current += ch;
      index += ch.length;
    }
    if (current) out.push(Object.freeze({ text: current, script: script || "Common", start }));
    return out;
  }
}

/** Turns text into phonemes. */
export interface Phonemizer {
  readonly isAvailable: boolean;
  phonemize(text: string, language: string): string;
}

/**
 * Passes text through unchanged.
 *
 * The right answer for a model whose front end takes GRAPHEMES, which several
 * do. Named so that choosing it is a decision rather than a fallback nobody
 * noticed.
 */
export class PassthroughPhonemizer implements Phonemizer {
  readonly isAvailable = true;
  phonemize(text: string): string {
    return text;
  }
}

/**
 * espeak-ng, out of process.
 *
 * OUT OF PROCESS BECAUSE ESPEAK IS GPL. Linking it would put this whole tree
 * under the GPL; running it as a program and reading its output does not. That
 * is a licensing constraint, not a design preference, and it is why this takes
 * a `run` callable rather than a binding.
 */
export class EspeakPhonemizer implements Phonemizer {
  constructor(private readonly run?: (text: string, voice: string) => string) {}

  get isAvailable(): boolean {
    return this.run !== undefined;
  }

  /**
   * Strips espeak's `(xx)` language-switch markers.
   *
   * espeak emits them when it decides a word belongs to another language, and
   * they are not phonemes - a model fed them pronounces the brackets.
   */
  static clean(raw: string): string {
    return raw.replace(/\([a-z-]{2,7}\)/g, "").replace(/\s+/g, " ").trim();
  }

  phonemize(text: string, language: string): string {
    if (!this.run) return text;
    return EspeakPhonemizer.clean(this.run(text, language));
  }
}

/** A phonemizer with its own binding, when a host has one. */
export class NativeEspeakPhonemizer extends EspeakPhonemizer {
  constructor(
    run?: (text: string, voice: string) => string,
    private readonly dataPath = "",
  ) {
    super(run);
  }

  get hasData(): boolean {
    return this.dataPath.length > 0;
  }
}

/** Where a pronunciation came from. */
export enum RespellingSource {
  /** The shipped dictionary. */
  Lexicon = "lexicon",
  /** Somebody corrected it on this device. Beats everything else. */
  Personal = "personal",
  /** A rule for a language's loanwords. */
  Loanword = "loanword",
  /** Nothing knew it; the phonemizer guessed. */
  Guessed = "guessed",
}

/** A pronunciation, and where it came from. */
export interface Respelling {
  readonly word: string;
  readonly pronunciation: string;
  readonly source: RespellingSource;
}

/**
 * Rewrites a word so a voice says it correctly.
 *
 * THE ORDER OF SOURCES IS THE DESIGN: personal beats loanword beats lexicon.
 * Somebody who has corrected how their own name is said must not be overruled
 * by a dictionary, ever - that correction is the single most valuable thing a
 * person will ever teach this.
 */
export class Respeller {
  private readonly personal = new Map<string, string>();
  private readonly lexicon = new Map<string, string>();
  private readonly loanwords = new Map<string, string>();

  learn(word: string, pronunciation: string): void {
    const key = word.trim().toLowerCase();
    if (key) this.personal.set(key, pronunciation);
  }

  addLexicon(entries: Readonly<Record<string, string>>): void {
    for (const [k, v] of Object.entries(entries)) this.lexicon.set(k.toLowerCase(), v);
  }

  addLoanwords(entries: Readonly<Record<string, string>>): void {
    for (const [k, v] of Object.entries(entries)) this.loanwords.set(k.toLowerCase(), v);
  }

  lookup(word: string): Respelling | undefined {
    const key = word.trim().toLowerCase();
    const personal = this.personal.get(key);
    if (personal) return Object.freeze({ word, pronunciation: personal, source: RespellingSource.Personal });
    const loan = this.loanwords.get(key);
    if (loan) return Object.freeze({ word, pronunciation: loan, source: RespellingSource.Loanword });
    const lex = this.lexicon.get(key);
    if (lex) return Object.freeze({ word, pronunciation: lex, source: RespellingSource.Lexicon });
    return undefined;
  }

  /** Rewrites a whole line, leaving unknown words alone. */
  apply(text: string): string {
    return text
      .split(/(\s+)/)
      .map((token) => {
        if (/^\s*$/.test(token)) return token;
        // Punctuation is stripped for the LOOKUP and put back afterwards, so
        // "Nkosi," matches "nkosi" and keeps its comma.
        const m = /^([^\p{L}\p{N}]*)(.*?)([^\p{L}\p{N}]*)$/u.exec(token);
        if (!m) return token;
        const found = this.lookup(m[2]);
        return found ? m[1] + found.pronunciation + m[3] : token;
      })
      .join("");
  }
}

/** A word somebody taught this device. */
export interface LearnedWord {
  readonly word: string;
  readonly pronunciation: string;
  readonly timesConfirmed: number;
  readonly lastUsedAtMs: number;
}

/** How confident the device is about a learned word. */
export enum LearningState {
  /** Heard once. Used, and easily overridden. */
  Provisional = "provisional",
  /** Confirmed more than once. Treated as correct. */
  Established = "established",
  /** Somebody corrected it back. Never offered again. */
  Rejected = "rejected",
}

/**
 * What this device has learned about how words are said HERE.
 *
 * ON DEVICE ONLY. A pronunciation is a fact about a person - their name, their
 * street, their family - and a corrections database that left the device would
 * be a map of who somebody knows.
 */
export class PersonalRespellings {
  private readonly words = new Map<string, LearnedWord>();
  private readonly rejected = new Set<string>();

  constructor(private readonly now: () => number = () => 0) {}

  /** Two confirmations to become established. One is a coincidence. */
  static readonly ESTABLISH_AT = 2;

  confirm(word: string, pronunciation: string): LearningState {
    const key = word.trim().toLowerCase();
    if (!key || this.rejected.has(key)) return LearningState.Rejected;
    const existing = this.words.get(key);
    const times = existing && existing.pronunciation === pronunciation ? existing.timesConfirmed + 1 : 1;
    this.words.set(key, Object.freeze({ word, pronunciation, timesConfirmed: times, lastUsedAtMs: this.now() }));
    return times >= PersonalRespellings.ESTABLISH_AT ? LearningState.Established : LearningState.Provisional;
  }

  /** A rejection is REMEMBERED, so the same wrong guess is not offered again. */
  reject(word: string): void {
    const key = word.trim().toLowerCase();
    this.words.delete(key);
    this.rejected.add(key);
  }

  get(word: string): LearnedWord | undefined {
    return this.words.get(word.trim().toLowerCase());
  }

  stateOf(word: string): LearningState {
    const key = word.trim().toLowerCase();
    if (this.rejected.has(key)) return LearningState.Rejected;
    const found = this.words.get(key);
    if (!found) return LearningState.Provisional;
    return found.timesConfirmed >= PersonalRespellings.ESTABLISH_AT
      ? LearningState.Established
      : LearningState.Provisional;
  }

  established(): Readonly<Record<string, string>> {
    const out: Record<string, string> = {};
    for (const [key, w] of this.words) {
      if (w.timesConfirmed >= PersonalRespellings.ESTABLISH_AT) out[key] = w.pronunciation;
    }
    return Object.freeze(out);
  }
}

/**
 * Ge'ez to Latin, and Ge'ez to phonemes.
 *
 * THE TABLE IS NOT HERE. `voice/geez_romanizer.ts` already carries it, and a
 * second transliteration table would be two tables that agree today and drift
 * the first time either is corrected - which for a script with 33 consonants
 * and 7 orders is a correction somebody will make. This delegates.
 *
 * Ge'ez is an ABUGIDA: each character is a consonant plus a vowel, in seven
 * orders. The sixth order is either a bare consonant or a schwa depending on
 * position, which is the rule a table alone cannot express - and the reason the
 * shared implementation is a function rather than a lookup.
 */
export class GeezRomanizer {
  static romanize(text: string): string {
    return romanizeGeez(text);
  }

  static isEthiopic(text: string): boolean {
    return isEthiopic(text);
  }
}

/** Ge'ez text to phonemes, via the shared romanisation. */
export class GeezPhonemizer implements Phonemizer {
  readonly isAvailable = true;
  constructor(private readonly inner: Phonemizer = new PassthroughPhonemizer()) {}
  phonemize(text: string, language: string): string {
    return this.inner.phonemize(GeezRomanizer.romanize(text), language);
  }
}

/** Where a tone mark comes from. */
export interface ToneSource {
  toneFor(word: string): readonly number[];
}

/**
 * Applies tone to a phoneme string.
 *
 * TONE IS LEXICAL in most of the languages here - it is not intonation, it
 * changes which word was said. A voice that ignores it says a different word
 * with complete confidence, and the listener has no way to tell.
 */
export class ToneShaper {
  constructor(private readonly source?: ToneSource) {}

  /** High and low, as the digits most models were trained on. */
  static readonly HIGH = 1;
  static readonly LOW = 0;

  apply(word: string, phonemes: string): string {
    const tones = this.source?.toneFor(word) ?? [];
    if (tones.length === 0) return phonemes;
    const syllables = phonemes.split(/(?<=[aeiouäəɛɔ])/);
    return syllables
      .map((s, i) => (i < tones.length ? s + (tones[i] === ToneShaper.HIGH ? "́" : "̀") : s))
      .join("");
  }
}

/** Applies a language's loanword rules. */
export class LoanwordRespeller {
  constructor(private readonly rules: ReadonlyArray<[RegExp, string]> = []) {}

  /**
   * Rules are applied IN ORDER and each sees the previous one's output, which
   * is what lets a general rule follow a specific one. Applying them all to the
   * original would let two rules both fire on the same span.
   */
  respell(word: string): string {
    return this.rules.reduce((w, [pattern, replacement]) => w.replace(pattern, replacement), word);
  }
}

/**
 * Nguni loanword rules.
 *
 * Nguni languages have no consonant clusters and no closed syllables, so an
 * English loanword acquires vowels - "school" becomes "isikole". A voice that
 * says the English form is speaking English inside a Zulu sentence.
 */
export class NguniRespeller extends LoanwordRespeller {
  constructor() {
    super([
      [/^s([ktp])/i, "isi$1"],
      [/([bcdfghjklmnpqrstvwxyz])$/i, "$1i"],
      [/([bcdfghjklmnpqrstvwxyz])([bcdfghjklmnpqrstvwxyz])/gi, "$1i$2"],
    ]);
  }
}

/** A dictionary-driven phonemizer. */
export class LexiconPhonemizer implements Phonemizer {
  constructor(
    private readonly entries: ReadonlyMap<string, string> = new Map(),
    private readonly fallback: Phonemizer = new PassthroughPhonemizer(),
    private readonly tone?: ToneShaper,
  ) {}

  get isAvailable(): boolean {
    return this.entries.size > 0 || this.fallback.isAvailable;
  }

  phonemize(text: string, language: string): string {
    return text
      .split(/(\s+)/)
      .map((token) => {
        if (/^\s*$/.test(token)) return token;
        const known = this.entries.get(token.toLowerCase());
        const phonemes = known ?? this.fallback.phonemize(token, language);
        return this.tone ? this.tone.apply(token, phonemes) : phonemes;
      })
      .join("");
  }
}

/** Japanese, through Open JTalk's prosody. */
export class OpenJTalkPhonemizer implements Phonemizer {
  constructor(private readonly run?: (text: string) => string) {}
  get isAvailable(): boolean {
    return this.run !== undefined;
  }
  phonemize(text: string): string {
    return this.run ? this.run(text) : text;
  }
}

/**
 * Reads Open JTalk's full-context labels into accent phrases.
 *
 * The labels carry ACCENT POSITION, which Japanese needs and a plain phoneme
 * string cannot express - the same phonemes with a different accent are a
 * different word. So the tokeniser keeps the position rather than discarding it
 * with the rest of the label.
 */
export class OpenJTalkProsodyTokeniser {
  static tokenise(labels: readonly string[]): { phoneme: string; accent: number }[] {
    const out: { phoneme: string; accent: number }[] = [];
    for (const label of labels) {
      const phoneme = /-([^+]+)\+/.exec(label)?.[1];
      if (!phoneme || phoneme === "sil" || phoneme === "pau") continue;
      const accent = Number(/\/A:([+-]?\d+)/.exec(label)?.[1] ?? "0");
      out.push({ phoneme, accent });
    }
    return out;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tokenising

/** Which SentencePiece flavour a model was trained with. */
export enum SentencePieceKind {
  /** Longest-match pieces with a score. What most TTS front ends use. */
  Unigram = "unigram",
  /** Merge rules. Deterministic, and a different algorithm entirely. */
  Bpe = "bpe",
}

/** One piece in the vocabulary. */
export interface SentencePiece {
  readonly piece: string;
  readonly id: number;
  readonly score: number;
}

/**
 * SentencePiece, enough of it to feed a voice model.
 *
 * The leading-space marker is `▁` (U+2581), NOT an underscore. They look alike
 * in some fonts and a vocabulary keyed on the wrong one matches nothing, so
 * every token falls back to unknown and the model receives noise.
 */
export class SentencePieceTokenizer {
  static readonly SPACE = "▁";

  private readonly byPiece = new Map<string, SentencePiece>();

  constructor(
    pieces: readonly SentencePiece[] = [],
    readonly kind: SentencePieceKind = SentencePieceKind.Unigram,
    readonly unknownId = 0,
  ) {
    for (const p of pieces) this.byPiece.set(p.piece, p);
  }

  get size(): number {
    return this.byPiece.size;
  }

  /**
   * Normalises the way SentencePiece does: NFKC, then spaces to the marker,
   * with a marker PREPENDED.
   *
   * The leading marker matters - a model trained with it sees "hello" and
   * "▁hello" as different tokens, and feeding the wrong one changes the
   * pronunciation of the first word of every sentence.
   */
  static normalise(text: string): string {
    const nfkc = text.normalize ? text.normalize("NFKC") : text;
    return SentencePieceTokenizer.SPACE + nfkc.trim().replace(/\s+/g, SentencePieceTokenizer.SPACE);
  }

  /**
   * Longest-match-first, which is what Unigram inference does in practice.
   *
   * Shortest-first would tokenise "the" as three characters and produce a token
   * sequence the model has never seen.
   */
  encode(text: string): number[] {
    const normalised = SentencePieceTokenizer.normalise(text);
    const out: number[] = [];
    let i = 0;
    while (i < normalised.length) {
      let matched: SentencePiece | undefined;
      for (let end = normalised.length; end > i; end--) {
        const candidate = this.byPiece.get(normalised.slice(i, end));
        if (candidate) {
          matched = candidate;
          i = end;
          break;
        }
      }
      if (!matched) {
        // An unknown character consumes exactly ONE code point, not one code
        // UNIT - stepping by one unit splits an emoji or an Ethiopic character
        // in half and emits two unknowns where the text had one character.
        out.push(this.unknownId);
        i += [...normalised.slice(i)][0]?.length ?? 1;
      } else out.push(matched.id);
    }
    return out;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Waking

/** A phrase the device wakes on. */
export interface WakePhrase {
  readonly phrase: string;
  readonly language: string;
  /** Per phrase, because a short phrase needs a higher bar than a long one. */
  readonly threshold: number;
}

/** Whether a phrase is usable as a wake word. */
export enum WakePhraseVerdict {
  Good = "good",
  /** Under two syllables. Fires on half of ordinary speech. */
  TooShort = "too-short",
  /** Long enough that people will not finish saying it. */
  TooLong = "too-long",
  /** Sounds like something common. "Hey there" wakes on "hey, there you are". */
  TooCommon = "too-common",
}

/**
 * The phrases this device knows, and whether they are any good.
 *
 * A WAKE PHRASE IS A TRADE and the phrase book is where it is made honestly. A
 * short phrase is easy to say and fires on the television; a long one never
 * false-fires and nobody finishes it.
 */
export class WakePhraseBook {
  private readonly phrases = new Map<string, WakePhrase>();

  /** Normalised: case folded, punctuation dropped, spaces collapsed. */
  static normalise(phrase: string): string {
    return [...phrase.toLowerCase()]
      .filter((c) => /[\p{L}\p{N}\s]/u.test(c))
      .join("")
      .split(/\s+/)
      .filter(Boolean)
      .join(" ");
  }

  /** A rough syllable count - vowel groups. Good enough to judge length. */
  static syllables(phrase: string): number {
    return (phrase.toLowerCase().match(/[aeiouyà-ü]+/g) ?? []).length;
  }

  private static readonly common = new Set(["hey there", "ok", "hello", "hi", "yes", "no"]);

  static judge(phrase: string): WakePhraseVerdict {
    const normalised = WakePhraseBook.normalise(phrase);
    if (WakePhraseBook.common.has(normalised)) return WakePhraseVerdict.TooCommon;
    const syllables = WakePhraseBook.syllables(normalised);
    if (syllables < 2) return WakePhraseVerdict.TooShort;
    if (syllables > 8) return WakePhraseVerdict.TooLong;
    return WakePhraseVerdict.Good;
  }

  /** Refuses a bad phrase rather than accepting it and disappointing later. */
  add(phrase: string, language = "", threshold = 0.62): WakePhraseVerdict {
    const verdict = WakePhraseBook.judge(phrase);
    if (verdict !== WakePhraseVerdict.Good) return verdict;
    const key = WakePhraseBook.normalise(phrase);
    this.phrases.set(key, Object.freeze({ phrase, language, threshold }));
    return verdict;
  }

  match(heard: string): WakePhrase | undefined {
    return this.phrases.get(WakePhraseBook.normalise(heard));
  }

  all(): readonly WakePhrase[] {
    return Object.freeze([...this.phrases.values()]);
  }
}

/** Something that might have been the wake word. */
export interface WakeCandidate {
  readonly phrase: string;
  readonly score: number;
  readonly atMs: number;
  /** The audio just before it. A person usually starts the request in the same
   * breath, and discarding it makes them repeat themselves. */
  readonly lookbackMs: number;
}

/** Decides whether a candidate really was the wake word. */
export interface WakeConfirmer {
  confirm(candidate: WakeCandidate): Promise<boolean>;
}

/**
 * Confirms everything.
 *
 * The right choice when the spotter is already strict, and named so that using
 * it is visible - a device with this and a loose threshold wakes to the
 * television.
 */
export class AlwaysConfirm implements WakeConfirmer {
  async confirm(): Promise<boolean> {
    return true;
  }
}

/**
 * Confirms by transcribing the audio and checking what was actually said.
 *
 * SLOWER AND FAR MORE ACCURATE, which is the right trade at this point: the
 * spotter has already decided something happened, so the cost is paid rarely
 * and it is what stops the device answering the radio.
 */
export class TranscriptConfirmer implements WakeConfirmer {
  constructor(
    private readonly transcribe?: (lookbackMs: number) => Promise<string>,
    private readonly book: WakePhraseBook = new WakePhraseBook(),
  ) {}

  async confirm(candidate: WakeCandidate): Promise<boolean> {
    if (!this.transcribe) return false;
    let heard: string;
    try {
      heard = await this.transcribe(candidate.lookbackMs);
    } catch {
      // A transcriber that failed means UNCONFIRMED, not confirmed. Waking on a
      // failure is how a device that cannot hear becomes a device that is
      // always listening.
      return false;
    }
    const normalised = WakePhraseBook.normalise(heard);
    return this.book.all().some((p) => normalised.includes(WakePhraseBook.normalise(p.phrase)));
  }
}

/**
 * Confirms by checking that speech actually STARTED at the candidate.
 *
 * A wake word detected in the middle of continuous speech is almost always a
 * false fire - somebody talking about something else. Requiring an onset is
 * cheap and rejects most of them.
 */
export class UtteranceOnsetConfirmer implements WakeConfirmer {
  constructor(
    private readonly energyBefore?: (atMs: number, windowMs: number) => number,
    private readonly quietThreshold = 0.02,
  ) {}

  async confirm(candidate: WakeCandidate): Promise<boolean> {
    if (!this.energyBefore) return false;
    // Quiet BEFORE the candidate means an utterance began there.
    return this.energyBefore(candidate.atMs, 400) < this.quietThreshold;
  }
}

/**
 * Either confirmer is enough.
 *
 * OR rather than AND on purpose: a transcript confirmer that times out should
 * not veto an onset that was unambiguous. Requiring both makes the device
 * miss wakes, which is the failure people actually notice.
 */
export class EitherConfirmer implements WakeConfirmer {
  constructor(private readonly confirmers: readonly WakeConfirmer[]) {}
  async confirm(candidate: WakeCandidate): Promise<boolean> {
    for (const c of this.confirmers) {
      if (await c.confirm(candidate)) return true;
    }
    return false;
  }
}

/** A wake detection. */
export interface KwsDetection {
  readonly phrase: string;
  readonly score: number;
  readonly atMs: number;
  readonly lookbackMs: number;
}

/** How close the current audio is, for a UI that shows listening. */
export interface KwsProgress {
  readonly score: number;
  readonly threshold: number;
  readonly framesHeld: number;
}

/** One keyword the spotter listens for. */
export interface KwsKeyword {
  readonly phrase: string;
  readonly threshold: number;
}

/** How the wake detector is tuned. */
export interface ZipformerWakeConfig {
  readonly threshold: number;
  /** Ignore anything for this long after a detection. Without it one utterance
   * fires on several consecutive frames and the assistant answers itself. */
  readonly refractoryMs: number;
  /** Frames the score must hold. A single frame over is usually a door. */
  readonly consecutiveFrames: number;
  readonly sampleRateHz: number;
  readonly frameMs: number;
}

export const zipformerWakeConfig = (
  partial: Partial<ZipformerWakeConfig> = {},
): ZipformerWakeConfig =>
  Object.freeze({
    // Leans towards MISSING. A missed wake is an annoyance; a false wake is a
    // microphone opening in a room where nobody asked it to.
    threshold: partial.threshold ?? 0.62,
    refractoryMs: partial.refractoryMs ?? 900,
    consecutiveFrames: partial.consecutiveFrames ?? 2,
    sampleRateHz: partial.sampleRateHz ?? 16000,
    frameMs: partial.frameMs ?? 30,
  });

/**
 * Streaming keyword spotting over a scoring callable.
 *
 * The hold and the refractory period are counted in AUDIO TIME rather than wall
 * time, so a device that stalls for a garbage collection does not silently
 * change the tuning.
 */
export class ZipformerKwsSpotter {
  private held = 0;
  private elapsedMs = 0;
  private mutedUntilMs = 0;
  private last: KwsProgress;

  constructor(
    readonly config: ZipformerWakeConfig = zipformerWakeConfig(),
    private readonly score?: (frame: readonly number[]) => { phrase: string; score: number },
  ) {
    this.last = Object.freeze({ score: 0, threshold: config.threshold, framesHeld: 0 });
  }

  get progress(): KwsProgress {
    return this.last;
  }

  reset(): void {
    this.held = 0;
    this.mutedUntilMs = this.elapsedMs;
  }

  push(frame: readonly number[]): KwsDetection | undefined {
    this.elapsedMs += this.config.frameMs;
    if (!this.score) return undefined;
    const { phrase, score } = this.score(frame);

    if (this.elapsedMs < this.mutedUntilMs) {
      // Still reported, so a UI does not freeze during the refractory period -
      // it just cannot fire.
      this.last = Object.freeze({ score, threshold: this.config.threshold, framesHeld: 0 });
      return undefined;
    }

    this.held = score >= this.config.threshold ? this.held + 1 : 0;
    this.last = Object.freeze({ score, threshold: this.config.threshold, framesHeld: this.held });

    if (this.held >= this.config.consecutiveFrames) {
      this.held = 0;
      this.mutedUntilMs = this.elapsedMs + this.config.refractoryMs;
      return Object.freeze({ phrase, score, atMs: this.elapsedMs, lookbackMs: 500 });
    }
    return undefined;
  }
}

/** A wake word over the spotter, with the phrase book. */
export class ZipformerWakeWordDetector {
  private readonly spotter: ZipformerKwsSpotter;

  constructor(
    private readonly book: WakePhraseBook = new WakePhraseBook(),
    config: ZipformerWakeConfig = zipformerWakeConfig(),
    score?: (frame: readonly number[]) => { phrase: string; score: number },
  ) {
    this.spotter = new ZipformerKwsSpotter(config, score);
  }

  get progress(): KwsProgress {
    return this.spotter.progress;
  }

  push(frame: readonly number[]): KwsDetection | undefined {
    const detection = this.spotter.push(frame);
    if (!detection) return undefined;
    // An EMPTY phrase book accepts any detection, so a build with no configured
    // phrase still wakes rather than being silently deaf.
    if (this.book.all().length === 0) return detection;
    return this.book.match(detection.phrase) ? detection : undefined;
  }
}

/** A spotter with a confirmation step. */
export class ConfirmedKeywordSpotter {
  constructor(
    private readonly detector: ZipformerWakeWordDetector,
    private readonly confirmer: WakeConfirmer = new AlwaysConfirm(),
  ) {}

  async push(frame: readonly number[]): Promise<KwsDetection | undefined> {
    const detection = this.detector.push(frame);
    if (!detection) return undefined;
    const confirmed = await this.confirmer.confirm(
      Object.freeze({
        phrase: detection.phrase,
        score: detection.score,
        atMs: detection.atMs,
        lookbackMs: detection.lookbackMs,
      }),
    );
    return confirmed ? detection : undefined;
  }
}

/** A graph of what may follow what, for a multi-word wake phrase. */
export interface KwsContextState {
  readonly index: number;
  readonly token: string;
}

/**
 * Tracks progress through a multi-word phrase.
 *
 * A WRONG TOKEN RESETS TO THE START, not to the previous state. Somebody who
 * says "hey... hey B" should wake, and a graph that only steps back one state
 * gets stuck partway through a phrase it will never complete.
 */
export class KwsContextGraph {
  private position = 0;

  constructor(private readonly tokens: readonly string[]) {}

  get state(): KwsContextState {
    return Object.freeze({ index: this.position, token: this.tokens[this.position] ?? "" });
  }

  get isComplete(): boolean {
    return this.position >= this.tokens.length;
  }

  accept(token: string): boolean {
    const wanted = this.tokens[this.position];
    if (wanted !== undefined && token.toLowerCase() === wanted.toLowerCase()) {
      this.position += 1;
      return true;
    }
    // Restarting on the FIRST token rather than resetting to zero blindly, so a
    // repeated first word does not lose its own progress.
    this.position = this.tokens[0]?.toLowerCase() === token.toLowerCase() ? 1 : 0;
    return false;
  }

  reset(): void {
    this.position = 0;
  }
}

/** How the wake stack is set up on this device. */
export interface WakeHostCapabilities {
  readonly canRunNeuralSpotter: boolean;
  readonly canTranscribeForConfirmation: boolean;
  readonly hasVoiceActivityDetector: boolean;
}

/** A calibration run's result. */
export interface WakeCalibration {
  readonly threshold: number;
  readonly falseFiresPerHour: number;
  readonly missRatePercent: number;
  /** Whether the run had enough samples to mean anything. */
  readonly isReliable: boolean;
}

/** Which languages the wake stack covers. */
export class WakeLanguages {
  static readonly SUPPORTED = Object.freeze([
    "en", "af", "zu", "xh", "st", "tn", "ts", "ve", "nr", "ss", "nso", "sw",
  ]);

  static covers(language: string): boolean {
    return WakeLanguages.SUPPORTED.includes(language.split(/[-_]/)[0].toLowerCase());
  }
}

/** Which language a wake phrase is judged in. */
export interface WakeLanguageChoice {
  readonly language: string;
  /** True when the language was assumed rather than told. Carried so a
   * diagnostics screen can say why a phrase behaves oddly. */
  readonly wasInferred: boolean;
}

/** Which spotter a host ended up with. */
export enum WakeEngine {
  /** The neural spotter. Best, and needs a model. */
  Zipformer = "zipformer",
  /** Transcribe everything and look for the phrase. Accurate and expensive. */
  Transcript = "transcript",
  /** Nothing available. The device does not wake; it is pressed. */
  None = "none",
}

/**
 * Builds the wake stack this host can actually run.
 *
 * IT NEVER RETURNS SOMETHING THAT WILL FAIL LATER. A host with no model gets
 * WakeEngine.None and a device that must be pressed, which is a worse
 * experience and an honest one - rather than a spotter that reports ready and
 * then never fires.
 */
export class WakeWordFactory {
  static choose(capabilities: WakeHostCapabilities): WakeEngine {
    if (capabilities.canRunNeuralSpotter) return WakeEngine.Zipformer;
    if (capabilities.canTranscribeForConfirmation) return WakeEngine.Transcript;
    return WakeEngine.None;
  }

  static confirmerFor(
    capabilities: WakeHostCapabilities,
    transcribe?: (lookbackMs: number) => Promise<string>,
    energyBefore?: (atMs: number, windowMs: number) => number,
    book: WakePhraseBook = new WakePhraseBook(),
  ): WakeConfirmer {
    const confirmers: WakeConfirmer[] = [];
    if (capabilities.canTranscribeForConfirmation && transcribe) {
      confirmers.push(new TranscriptConfirmer(transcribe, book));
    }
    if (capabilities.hasVoiceActivityDetector && energyBefore) {
      confirmers.push(new UtteranceOnsetConfirmer(energyBefore));
    }
    // No confirmer at all means AlwaysConfirm, which is only safe because the
    // spotter's own threshold is the strict one. Named so the trade is visible.
    return confirmers.length === 0 ? new AlwaysConfirm() : new EitherConfirmer(confirmers);
  }

  /**
   * A calibration is only reliable with enough listening behind it.
   *
   * Reporting a threshold from ten minutes of audio is reporting the room, not
   * the phrase.
   */
  static calibrate(
    falseFires: number,
    hoursListened: number,
    misses: number,
    attempts: number,
    threshold: number,
  ): WakeCalibration {
    return Object.freeze({
      threshold,
      falseFiresPerHour: hoursListened > 0 ? falseFires / hoursListened : 0,
      missRatePercent: attempts > 0 ? (misses / attempts) * 100 : 0,
      isReliable: hoursListened >= 4 && attempts >= 20,
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Speaking

/** Plays audio. */
export interface AudioPlayer {
  readonly isAvailable: boolean;
  play(format: AudioPcmFormat, samples: readonly number[]): Promise<void>;
  stop(): Promise<void>;
}

/** Plays nothing. */
export class NullAudioPlayer implements AudioPlayer {
  readonly isAvailable = false;
  async play(): Promise<void> {
    /* nothing to play on */
  }
  async stop(): Promise<void> {
    /* nothing to stop */
  }
}

/** Turns text into audio. */
export interface TtsEngine {
  readonly isAvailable: boolean;
  synthesize(text: string, language: string): Promise<{ format: AudioPcmFormat; samples: number[] }>;
}

/** What a front end can report about itself. */
export interface TtsFrontEndDiagnostics {
  readonly phonemizer: string;
  readonly hasLexicon: boolean;
  readonly hasPersonalRespellings: boolean;
  readonly supportedLanguages: readonly string[];
}

/**
 * Speaks a long passage phrase by phrase.
 *
 * SPLIT SO THE FIRST WORDS START SOONER. Synthesising a whole paragraph before
 * any of it plays is a wait the listener reads as the device being broken; the
 * total time is the same and the perceived time is not.
 */
export class PhrasedTtsEngine implements TtsEngine {
  constructor(
    private readonly inner: TtsEngine,
    private readonly maxCharacters = 180,
  ) {}

  get isAvailable(): boolean {
    return this.inner.isAvailable;
  }

  /** Splits on SENTENCES first and only then on length, so a break never lands
   * mid-clause where it would sound like a stumble. */
  phrases(text: string): string[] {
    const out: string[] = [];
    for (const sentence of SentenceSplitter.split(text)) {
      if (sentence.length <= this.maxCharacters) {
        out.push(sentence);
        continue;
      }
      let current = "";
      for (const clause of sentence.split(/(?<=[,;:])\s+/)) {
        if (current && current.length + clause.length > this.maxCharacters) {
          out.push(current);
          current = clause;
        } else current = current ? `${current} ${clause}` : clause;
      }
      if (current) out.push(current);
    }
    return out;
  }

  async synthesize(text: string, language: string) {
    const parts = this.phrases(text);
    const all: number[] = [];
    let format = SPEECH_FORMAT;
    for (const part of parts) {
      const result = await this.inner.synthesize(part, language);
      format = result.format;
      all.push(...result.samples);
    }
    return { format, samples: all };
  }
}

/** A TTS engine that applies respellings before synthesising. */
export class RespellingTtsEngine implements TtsEngine {
  constructor(
    private readonly inner: TtsEngine,
    private readonly respeller: Respeller = new Respeller(),
  ) {}
  get isAvailable(): boolean {
    return this.inner.isAvailable;
  }
  async synthesize(text: string, language: string) {
    return this.inner.synthesize(this.respeller.apply(text), language);
  }
}

/** An ONNX voice, when one is loaded. */
export class ToucanOnnxTtsEngine implements TtsEngine {
  constructor(
    private readonly run?: (phonemes: string) => Promise<number[]>,
    private readonly phonemizer: Phonemizer = new PassthroughPhonemizer(),
    private readonly sampleRateHz = 22050,
  ) {}
  get isAvailable(): boolean {
    return this.run !== undefined;
  }
  async synthesize(text: string, language: string) {
    if (!this.run) throw new Error("no voice is loaded on this device");
    return {
      format: audioPcmFormat(this.sampleRateHz, 1, 16),
      samples: await this.run(this.phonemizer.phonemize(text, language)),
    };
  }
}

/** Kokoro, which takes graphemes rather than phonemes. */
export class KokoroTtsEngine implements TtsEngine {
  constructor(
    private readonly run?: (text: string, voice: string) => Promise<number[]>,
    private readonly voice = "",
    private readonly sampleRateHz = 24000,
  ) {}
  get isAvailable(): boolean {
    return this.run !== undefined && this.voice.length > 0;
  }
  async synthesize(text: string) {
    if (!this.run) throw new Error("no voice is loaded on this device");
    return {
      format: audioPcmFormat(this.sampleRateHz, 1, 16),
      samples: await this.run(text, this.voice),
    };
  }
}

/**
 * PocketTTS, where the voice rides on the text input.
 *
 * NaN marks the beginning of a sequence, and EOS is NOT a stop - the model
 * emits it and keeps going, so a caller that stops there truncates the last
 * word of every utterance.
 */
export class PocketTtsEngine implements TtsEngine {
  constructor(
    private readonly run?: (tokens: readonly number[], reference: readonly number[]) => Promise<number[]>,
    private readonly tokenizer: SentencePieceTokenizer = new SentencePieceTokenizer(),
    private readonly reference: readonly number[] = [],
    private readonly sampleRateHz = 24000,
  ) {}

  get isAvailable(): boolean {
    return this.run !== undefined && this.reference.length > 0;
  }

  async synthesize(text: string) {
    if (!this.run) throw new Error("no voice is loaded on this device");
    return {
      format: audioPcmFormat(this.sampleRateHz, 1, 16),
      samples: await this.run(this.tokenizer.encode(text), this.reference),
    };
  }
}

/** Transcribes audio. */
export interface Transcriber {
  readonly isAvailable: boolean;
  transcribe(format: AudioPcmFormat, samples: readonly number[], language?: string): Promise<string>;
}

/**
 * Whisper through a host binding.
 *
 * THE RATE CHECK IS THE POINT. Whisper wants 16 kHz mono, and feeding it 22050
 * does not fail - it transcribes audio it believes is slower than it is and
 * produces confident nonsense.
 */
export class WhisperTranscriber implements Transcriber {
  constructor(
    private readonly run?: (samples: readonly number[], language: string) => Promise<string>,
    private readonly language = "",
  ) {}

  get isAvailable(): boolean {
    return this.run !== undefined;
  }

  /** Downmixes and resamples to what the model needs. */
  prepare(format: AudioPcmFormat, samples: readonly number[]): number[] {
    let mono = [...samples];
    if (format.channels > 1) {
      // AVERAGED, not left-channel-only. Taking one channel loses anything
      // panned away from it, and a phone's two microphones are the same voice
      // with different noise rather than a stereo image.
      const n = format.channels;
      const out: number[] = [];
      for (let i = 0; i + n <= mono.length; i += n) {
        let sum = 0;
        for (let c = 0; c < n; c++) sum += mono[i + c];
        out.push(sum / n);
      }
      mono = out;
    }
    return format.sampleRateHz === SPEECH_FORMAT.sampleRateHz
      ? mono
      : WavIo.resampleLinear(mono, format.sampleRateHz, SPEECH_FORMAT.sampleRateHz);
  }

  async transcribe(format: AudioPcmFormat, samples: readonly number[], language = ""): Promise<string> {
    if (!this.run) throw new Error("no transcription engine is loaded on this device");
    return this.run(this.prepare(format, samples), language || this.language);
  }
}

/** The managed binding, which also needs a model file. */
export class WhisperNetTranscriber extends WhisperTranscriber {
  constructor(
    run?: (samples: readonly number[], language: string) => Promise<string>,
    language = "",
    readonly modelPath = "",
  ) {
    super(run, language);
  }

  /** Needs BOTH a model file and a binding. Either alone is a transcriber that
   * reports ready and then fails on the first call. */
  get isAvailable(): boolean {
    return super.isAvailable && this.modelPath.length > 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The loop

/** One exchange: what was heard, and what was said back. */
export interface VoiceExchangeEvent {
  readonly heard: string;
  readonly said: string;
  readonly listenMs: number;
  readonly thinkMs: number;
  readonly speakMs: number;
  readonly wasBargedIn: boolean;
}

/** Where the time went in one exchange. */
export class VoiceTrace {
  private readonly marks = new Map<string, number>();
  private readonly spans: { name: string; ms: number }[] = [];

  constructor(private readonly now: () => number = () => 0) {}

  start(name: string): void {
    this.marks.set(name, this.now());
  }

  end(name: string): number {
    const started = this.marks.get(name);
    if (started === undefined) return 0;
    const ms = this.now() - started;
    this.marks.delete(name);
    this.spans.push({ name, ms });
    return ms;
  }

  /**
   * The slowest span, which is what to fix.
   *
   * A total tells you it was slow; the breakdown tells you whether it was the
   * microphone, the model or the voice - and those are three different jobs.
   */
  slowest(): { name: string; ms: number } | undefined {
    return this.spans.reduce<{ name: string; ms: number } | undefined>(
      (worst, s) => (!worst || s.ms > worst.ms ? s : worst),
      undefined,
    );
  }

  totalMs(): number {
    return this.spans.reduce((n, s) => n + s.ms, 0);
  }

  summary(): string {
    if (this.spans.length === 0) return "nothing timed";
    return this.spans.map((s) => `${s.name} ${Math.round(s.ms)}ms`).join(", ");
  }
}

/**
 * Listen, think, speak - and be interruptible throughout.
 *
 * BARGE-IN IS NOT A FEATURE, it is the difference between a voice assistant
 * that works and one people stop using. Without it the device keeps talking
 * over somebody who has started speaking, and there is no way to stop it but to
 * wait.
 */
export class VoiceLoop {
  private speaking = false;
  private bargedIn = false;
  private readonly handlers: ((event: VoiceExchangeEvent) => void)[] = [];

  constructor(
    private readonly transcriber: Transcriber,
    private readonly tts: TtsEngine,
    private readonly player: AudioPlayer = new NullAudioPlayer(),
    private readonly respond?: (heard: string) => Promise<string>,
    private readonly now: () => number = () => 0,
  ) {}

  onExchange(handler: (event: VoiceExchangeEvent) => void): void {
    this.handlers.push(handler);
  }

  get isSpeaking(): boolean {
    return this.speaking;
  }

  /** Stops the voice immediately. Safe to call when nothing is speaking. */
  async bargeIn(): Promise<void> {
    if (!this.speaking) return;
    this.bargedIn = true;
    await this.player.stop();
    this.speaking = false;
  }

  async exchange(
    format: AudioPcmFormat,
    samples: readonly number[],
    language = "",
  ): Promise<VoiceExchangeEvent> {
    const trace = new VoiceTrace(this.now);
    this.bargedIn = false;

    trace.start("listen");
    const heard = this.transcriber.isAvailable
      ? await this.transcriber.transcribe(format, samples, language)
      : "";
    const listenMs = trace.end("listen");

    trace.start("think");
    const said = this.respond && heard.trim() ? await this.respond(heard) : "";
    const thinkMs = trace.end("think");

    trace.start("speak");
    if (said && this.tts.isAvailable && this.player.isAvailable) {
      this.speaking = true;
      try {
        const audio = await this.tts.synthesize(said, language);
        // Checked AGAIN after synthesis: somebody can barge in while the voice
        // is still being generated, and playing it anyway is exactly the
        // behaviour barge-in exists to prevent.
        if (!this.bargedIn) await this.player.play(audio.format, audio.samples);
      } finally {
        this.speaking = false;
      }
    }
    const speakMs = trace.end("speak");

    const event: VoiceExchangeEvent = Object.freeze({
      heard,
      said,
      listenMs,
      thinkMs,
      speakMs,
      wasBargedIn: this.bargedIn,
    });
    for (const handler of this.handlers) {
      // A raising handler must not stop the others, or the loop itself.
      try {
        handler(event);
      } catch {
        continue;
      }
    }
    return event;
  }
}

// The C# spellings, kept so the two trees line up.
export type IPhonemizer = Phonemizer;
export type IToneSource = ToneSource;
export type IAudioPlayer = AudioPlayer;
export type ITtsEngine = TtsEngine;
export type ITtsFrontEndDiagnostics = TtsFrontEndDiagnostics;
export type ITranscriber = Transcriber;
export type IWakeConfirmer = WakeConfirmer;
export type VoiceExchangeEventArgs = VoiceExchangeEvent;
