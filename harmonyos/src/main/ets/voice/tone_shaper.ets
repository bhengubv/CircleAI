/**
 * voice/tone_shaper
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tone_shaper.ts
//
// Port of src/CircleAI.Voice/ToneShaper.cs.
//
// Parity is asserted against fixtures/voice_tone_shaper.json, which the C#
// reference generates.
//
// Warmth, after the model has finished.
//
// THE VOICE WAS REPORTED AS TINNY, AND THE SPEAKER COULD NOT FIX IT. Choosing a
// speaker by how well the recogniser understands it has a bias nobody costed:
// word error rate rewards crisp consonants and a bright top end, which is what
// "tinny" describes. Measured across all 130 speakers in the bundle, warmth and
// intelligibility are inversely related — the darkest voices are the ones the
// recogniser loses. So the speaker is not the lever. The waveform is, and it is
// entirely ours once the model hands it over.
//
// WHY A DIP AND NOT JUST A BOOST. A phone speaker cannot move enough air to
// reproduce a low-shelf boost; on a P30 the bass simply is not there to lift.
// Cutting 2-5 kHz, where harshness lives, works on hardware that cannot do
// bass, which is most of the hardware this ships to. The boost is for
// headphones. Both are applied because the product is used on both.

/** Biquad coefficients, already normalised by a0. */
export interface BiquadCoefficients {
  readonly b: readonly number[];
  readonly a: readonly number[];
}

export interface ToneShaperSettings {
  /** Where the low shelf starts lifting, in Hz. */
  readonly lowShelfHz: number;
  /** How much to lift the bottom, in dB. */
  readonly lowShelfDb: number;
  /** Centre of the harshness dip, in Hz. */
  readonly presenceHz: number;
  /** How much to cut there, in dB. Negative cuts. */
  readonly presenceDb: number;
  /** Width of the dip. Lower is wider. */
  readonly presenceQ: number;
}

/** The measured setting: warmer, with no cost to intelligibility. */
export const WARM: ToneShaperSettings = Object.freeze({
  lowShelfHz: 320,
  lowShelfDb: 4.0,
  presenceHz: 3200,
  presenceDb: -4.0,
  presenceQ: 0.8,
});

const LOW_SHELF_SLOPE = 0.9;

function normalise(b: number[], a: number[]): BiquadCoefficients {
  const a0 = a[0];
  for (let i = 0; i < 3; i++) { b[i] /= a0; a[i] /= a0; }
  return { b, a };
}

/** RBJ audio-cookbook low shelf, normalised by a0. */
export function lowShelfCoefficients(s: ToneShaperSettings, rate: number): BiquadCoefficients {
  const A = Math.pow(10, s.lowShelfDb / 40);
  const w0 = 2 * Math.PI * s.lowShelfHz / rate;
  const alpha = Math.sin(w0) / 2 * Math.sqrt((A + 1 / A) * (1 / LOW_SHELF_SLOPE - 1) + 2);
  const c = Math.cos(w0);
  const s2 = 2 * Math.sqrt(A) * alpha;

  return normalise(
    [A * ((A + 1) - (A - 1) * c + s2),
     2 * A * ((A - 1) - (A + 1) * c),
     A * ((A + 1) - (A - 1) * c - s2)],
    [(A + 1) + (A - 1) * c + s2,
     -2 * ((A - 1) + (A + 1) * c),
     (A + 1) + (A - 1) * c - s2],
  );
}

/** RBJ audio-cookbook peaking EQ, normalised by a0. */
export function peakingCoefficients(s: ToneShaperSettings, rate: number): BiquadCoefficients {
  const A = Math.pow(10, s.presenceDb / 40);
  const w0 = 2 * Math.PI * s.presenceHz / rate;
  const alpha = Math.sin(w0) / (2 * s.presenceQ);
  const c = Math.cos(w0);

  return normalise(
    [1 + alpha * A, -2 * c, 1 - alpha * A],
    [1 + alpha / A, -2 * c, 1 - alpha / A],
  );
}

/**
 * Direct-form-I biquad, in place.
 *
 * THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
 * The filter memory never sees the float rounding — y1 keeps the full-precision
 * result — so the recursion is identical everywhere. Only what lands in the
 * buffer is narrowed, which is what the next stage then reads.
 */
export function biquad(x: Float32Array, coeffs: BiquadCoefficients): void {
  const { b, a } = coeffs;
  let x1 = 0, x2 = 0, y1 = 0, y2 = 0;
  for (let i = 0; i < x.length; i++) {
    const xn = x[i];
    const yn = b[0] * xn + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2;
    x2 = x1; x1 = xn;
    y2 = y1; y1 = yn;
    x[i] = Math.fround(yn);
  }
}

function peak(x: Float32Array): number {
  let p = 0;
  for (const v of x) { const a = Math.abs(v); if (a > p) p = a; }
  return p;
}

/**
 * Filters `waveform` in place with a low shelf and a presence dip in series.
 *
 * PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a
 * waveform that already peaked near full scale would clip — which is heard as
 * crackle and would be blamed on the quantised model rather than on this.
 * Scaling back to the original peak keeps the tone change audible and the level
 * unchanged.
 */
export function applyToneShaper(
  waveform: Float32Array,
  sampleRate: number,
  settings: ToneShaperSettings = WARM,
): void {
  if (waveform.length === 0 || sampleRate <= 0) return;

  const before = peak(waveform);
  if (before <= 0) return;   // a silent buffer, and dividing by that peak is NaN

  biquad(waveform, lowShelfCoefficients(settings, sampleRate));
  biquad(waveform, peakingCoefficients(settings, sampleRate));

  const after = peak(waveform);
  if (after > 0 && after > before) {
    // fround, because the reference divides two FLOATS here. Leaving it a double
    // makes the gain a few ULP different and the whole tail of the waveform
    // drifts with it — the kind of divergence that only shows up as a fixture
    // mismatch in the 40th sample, never as anything audible.
    const g = Math.fround(before / after);
    for (let i = 0; i < waveform.length; i++) waveform[i] = Math.fround(waveform[i] * g);
  }
}
