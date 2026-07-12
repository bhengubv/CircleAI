// telephony/answering_machine_detector.ts
//
// Heuristic AMD — faithful port of AnsweringMachineDetector.cs. Classify whether
// the answering side of an outbound call is a human or an answering machine,
// based on the length of the first contiguous speech burst and the timing of
// any follow-up audio. Cheaper than carrier-side AMD; runs on the audio frames
// we already have, no extra cost.
//
// `ReadOnlySpan<byte>` PCM-16 → Uint8Array read little-endian via DataView.
// `TimeSpan` accumulators → milliseconds. The float energy threshold uses
// Math.fround to match C# `float`.

const INT16_MAX = 32767;

/** Verdict from the answering-machine detector. Mirrors `AmdVerdict`. */
export const AmdVerdict = {
  Unknown: "Unknown",
  Human: "Human",
  AnsweringMachine: "AnsweringMachine",
} as const;
export type AmdVerdict = (typeof AmdVerdict)[keyof typeof AmdVerdict];

/** Heuristic AMD configuration. Mirrors `AmdOptions`. */
export interface AmdOptions {
  /** Above this length (ms), it's likely a machine. Default 1800 ms. */
  readonly humanMaxFirstUtteranceMs?: number;
  /** Below this (ms) it's too short to decide. Default 300 ms. */
  readonly humanMinFirstUtteranceMs?: number;
  /** Stop accumulating once this (ms) elapses. Default 3500 ms. */
  readonly maxObservationWindow?: number;
  /** Frames silent for this long (ms) end the current utterance. Default 250 ms. */
  readonly silenceFrameThresholdMs?: number;
}

function humanMaxFirst(o: AmdOptions): number {
  return o.humanMaxFirstUtteranceMs ?? 1800;
}
function humanMinFirst(o: AmdOptions): number {
  return o.humanMinFirstUtteranceMs ?? 300;
}
function maxObservation(o: AmdOptions): number {
  return o.maxObservationWindow ?? 3500;
}
function silenceThreshold(o: AmdOptions): number {
  return o.silenceFrameThresholdMs ?? 250;
}

const ENERGY_THRESHOLD = Math.fround(0.012);

function frameHasSpeech(pcm: Uint8Array): boolean {
  const view = new DataView(pcm.buffer, pcm.byteOffset, pcm.byteLength);
  const sampleCount = Math.trunc(pcm.byteLength / 2);
  let sumSquares = 0;
  for (let i = 0; i < sampleCount; i++) {
    const s = view.getInt16(i * 2, true);
    sumSquares += s * s;
  }
  const rms = Math.sqrt(sumSquares / sampleCount) / INT16_MAX;
  return rms >= ENERGY_THRESHOLD;
}

/**
 * Frame-by-frame AMD. Feed PCM-16 frames in until {@link currentVerdict}
 * stabilises. Mirrors `AnsweringMachineDetector`.
 */
export class AnsweringMachineDetector {
  private readonly options: AmdOptions;
  private firstUtteranceLengthMs = 0;
  private accumulatedAudioMs = 0;
  private utteranceInProgress = false;
  private trailingSilenceMs = 0;
  private verdict: AmdVerdict = AmdVerdict.Unknown;

  constructor(options?: AmdOptions) {
    this.options = options ?? {};
  }

  get currentVerdict(): AmdVerdict {
    return this.verdict;
  }

  /** Feed one frame of PCM-16 mono. Returns the (possibly updated) verdict. */
  observe(pcmFrame: Uint8Array, sampleRateHz: number): AmdVerdict {
    if (sampleRateHz <= 0) throw new RangeError("sampleRateHz");
    if (pcmFrame.byteLength < 2) return this.currentVerdict;

    const frameDurationMs = (1000 * Math.trunc(pcmFrame.byteLength / 2)) / sampleRateHz;
    const isSpeech = frameHasSpeech(pcmFrame);

    if (this.verdict !== AmdVerdict.Unknown) return this.verdict;

    this.accumulatedAudioMs += frameDurationMs;

    if (isSpeech) {
      if (!this.utteranceInProgress) {
        this.utteranceInProgress = true;
      }
      this.firstUtteranceLengthMs += frameDurationMs;
      this.trailingSilenceMs = 0;
    } else if (this.utteranceInProgress) {
      this.trailingSilenceMs += frameDurationMs;
      if (this.trailingSilenceMs >= silenceThreshold(this.options)) {
        this.utteranceInProgress = false;
      }
    }

    // Decide.
    const firstMs = this.firstUtteranceLengthMs;
    if (firstMs >= humanMaxFirst(this.options)) {
      this.verdict = AmdVerdict.AnsweringMachine;
    } else if (
      !this.utteranceInProgress &&
      firstMs >= humanMinFirst(this.options) &&
      firstMs < humanMaxFirst(this.options)
    ) {
      this.verdict = AmdVerdict.Human;
    } else if (this.accumulatedAudioMs >= maxObservation(this.options)) {
      this.verdict =
        firstMs < humanMinFirst(this.options) ? AmdVerdict.Unknown : AmdVerdict.AnsweringMachine;
    }
    return this.verdict;
  }

  reset(): void {
    this.firstUtteranceLengthMs = 0;
    this.accumulatedAudioMs = 0;
    this.utteranceInProgress = false;
    this.trailingSilenceMs = 0;
    this.verdict = AmdVerdict.Unknown;
  }
}
