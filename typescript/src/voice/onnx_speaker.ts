// voice/onnx_speaker.ts
//
// OnnxSpeakerIdentity.cs — neural speaker diarisation / identification via ONNX
// (ECAPA-TDNN-style embeddings). The ONNX runtime is injected behind IOnnxSession;
// the enrollment persistence (C# reads/writes a JSON file beside the model) is
// injected behind IEnrollmentStore with a Null default, matching the port's
// "inject the platform seam, keep the logic deterministic" convention.
//
// Enrollment averages all observed embeddings per user (L2-normalised centroid);
// identification is cosine-similarity against every centroid, winner above
// MatchThreshold. Ported one-to-one, including the min/max-utterance clamps.

import {
  type ISpeakerIdentity,
} from "./identity_contracts.js";
import { hammingWindow, l2Normalise, melFilterbank, powerSpectrum, readInt16LE } from "./dsp.js";
import { floatTensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend.js";

/** How the speaker-embedding model expects audio. */
export enum SpeakerEmbedderInputKind {
  LogMel = 0,
  RawWaveform = 1,
}

/** Per-user enrollment record used for cosine-similarity ID. Mirrors `EnrolledSpeaker`. */
export interface EnrolledSpeaker {
  readonly userId: string;
  /** L2-normalised centroid embedding. Serialised as a plain number[]. */
  readonly centroid: number[];
  readonly sampleCount: number;
}

/**
 * Persistence seam for the enrollment centroids. The C# implementation reads
 * `File.ReadAllText` on construction and atomically rewrites the JSON store on
 * every enroll. Injected here so the port needs no filesystem; the default is a
 * {@link NullEnrollmentStore}.
 */
export interface IEnrollmentStore {
  /** Load all persisted speakers (empty when the store is absent/unreadable). */
  loadAsync(): Promise<EnrolledSpeaker[]>;
  /** Persist the full set of speakers (atomic replace in real implementations). */
  saveAsync(speakers: EnrolledSpeaker[]): Promise<void>;
}

/** No-op {@link IEnrollmentStore}: nothing is persisted (in-memory enrollment only). */
export class NullEnrollmentStore implements IEnrollmentStore {
  async loadAsync(): Promise<EnrolledSpeaker[]> {
    return [];
  }
  async saveAsync(_speakers: EnrolledSpeaker[]): Promise<void> {
    /* nothing */
  }
}

/** Configuration for {@link OnnxSpeakerIdentity}. Mirrors `SpeakerIdentityConfig` record. */
export interface SpeakerIdentityConfig {
  readonly modelPath: string;
  readonly inputKind: SpeakerEmbedderInputKind;
  readonly sampleRateHz: number;
  readonly nMelBins: number;
  readonly melFrameMs: number;
  readonly melHopMs: number;
  readonly minUtteranceMs: number;
  readonly maxUtteranceMs: number;
  readonly matchThreshold: number;
}

/**
 * Build a {@link SpeakerIdentityConfig} with the C# record's defaults
 * (LogMel, 16 kHz, 80 mel bins, 25/10 ms STFT, 1000–8000 ms utterance,
 * 0.55 match threshold).
 */
export function speakerIdentityConfig(
  modelPath: string,
  overrides: Partial<Omit<SpeakerIdentityConfig, "modelPath">> = {},
): SpeakerIdentityConfig {
  if (!modelPath || modelPath.trim().length === 0) throw new Error("modelPath is required");
  return {
    modelPath,
    inputKind: overrides.inputKind ?? SpeakerEmbedderInputKind.LogMel,
    sampleRateHz: overrides.sampleRateHz ?? 16_000,
    nMelBins: overrides.nMelBins ?? 80,
    melFrameMs: overrides.melFrameMs ?? 25,
    melHopMs: overrides.melHopMs ?? 10,
    minUtteranceMs: overrides.minUtteranceMs ?? 1_000,
    maxUtteranceMs: overrides.maxUtteranceMs ?? 8_000,
    matchThreshold: overrides.matchThreshold ?? 0.55,
  };
}

export class OnnxSpeakerIdentity implements ISpeakerIdentity {
  private readonly config: SpeakerIdentityConfig;
  private readonly sessionFactory: OnnxSessionFactory;
  private readonly store: IEnrollmentStore;
  private readonly enrolled = new Map<string, EnrolledSpeaker>();

  private session: IOnnxSession | null = null;
  private loadedStore = false;
  private disposed = false;

  constructor(
    config: SpeakerIdentityConfig,
    sessionFactory: OnnxSessionFactory,
    store: IEnrollmentStore = new NullEnrollmentStore(),
  ) {
    if (config == null) throw new Error("config is required");
    if (sessionFactory == null) throw new Error("sessionFactory is required");
    this.config = config;
    this.sessionFactory = sessionFactory;
    this.store = store;
  }

  async identifyAsync(audioPcm16: Uint8Array, sampleRateHz: number, signal?: AbortSignal): Promise<string | null> {
    if (this.disposed) throw new Error("OnnxSpeakerIdentity is disposed");
    await this.ensureStoreLoaded();
    if (audioPcm16.length === 0) return null;
    if (this.enrolled.size === 0) return null;
    if (signal?.aborted) return null;

    const embedding = this.computeEmbedding(audioPcm16, sampleRateHz);
    if (embedding === null) return null;

    let best: string | null = null;
    let bestSim = -Infinity;
    for (const [userId, speaker] of this.enrolled) {
      if (signal?.aborted) break;
      const sim = cosineSimilarity(embedding, speaker.centroid);
      if (sim > bestSim) {
        bestSim = sim;
        best = userId;
      }
    }
    return bestSim >= this.config.matchThreshold ? best : null;
  }

  async enrollAsync(
    userId: string,
    audioPcm16: Uint8Array,
    sampleRateHz: number,
    _signal?: AbortSignal,
  ): Promise<void> {
    if (this.disposed) throw new Error("OnnxSpeakerIdentity is disposed");
    if (!userId || userId.trim().length === 0) throw new Error("userId required");
    if (audioPcm16.length === 0) throw new Error("audio required");
    await this.ensureStoreLoaded();

    const embedding = this.computeEmbedding(audioPcm16, sampleRateHz);
    if (embedding === null) throw new Error("Embedding extraction failed");

    const prev = this.enrolled.get(userId);
    if (prev === undefined) {
      this.enrolled.set(userId, { userId, centroid: Array.from(embedding), sampleCount: 1 });
    } else {
      const n = prev.sampleCount;
      const newCentroid = new Float32Array(prev.centroid.length);
      for (let i = 0; i < newCentroid.length; i++) {
        newCentroid[i] = (prev.centroid[i] * n + embedding[i]) / (n + 1);
      }
      l2Normalise(newCentroid);
      this.enrolled.set(userId, { userId, centroid: Array.from(newCentroid), sampleCount: n + 1 });
    }
    await this.store.saveAsync([...this.enrolled.values()]);
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.session?.dispose();
    this.session = null;
  }

  private ensureSession(): IOnnxSession {
    if (this.session !== null) return this.session;
    this.session = this.sessionFactory(this.config.modelPath);
    return this.session;
  }

  private async ensureStoreLoaded(): Promise<void> {
    if (this.loadedStore) return;
    this.loadedStore = true;
    try {
      for (const r of await this.store.loadAsync()) this.enrolled.set(r.userId, r);
    } catch (ex) {
      if (typeof console !== "undefined" && console.error) {
        console.error(`[OnnxSpeakerIdentity] enrollment load failed: ${ex instanceof Error ? ex.message : String(ex)}`);
      }
    }
  }

  private computeEmbedding(pcm16: Uint8Array, sampleRateHz: number): Float32Array | null {
    try {
      if (sampleRateHz !== this.config.sampleRateHz) {
        if (typeof console !== "undefined" && console.error) {
          console.error(
            `[OnnxSpeakerIdentity] mismatched sample rate ${sampleRateHz} vs model ${this.config.sampleRateHz}`,
          );
        }
        return null;
      }
      const minSamples = Math.trunc((sampleRateHz * this.config.minUtteranceMs) / 1000);
      const maxSamples = Math.trunc((sampleRateHz * this.config.maxUtteranceMs) / 1000);
      let nSamples = Math.trunc(pcm16.length / 2);
      if (nSamples < minSamples) return null;
      if (nSamples > maxSamples) nSamples = maxSamples;

      const window = new Float32Array(nSamples);
      for (let i = 0; i < nSamples; i++) window[i] = readInt16LE(pcm16, i * 2) / 32768;

      const session = this.ensureSession();
      const tensor =
        this.config.inputKind === SpeakerEmbedderInputKind.RawWaveform
          ? floatTensor(window, [1, nSamples])
          : this.logMelTensor(window);

      const outputs = session.run({ [session.inputName]: tensor });
      const output = outputs[session.outputName].data.slice();
      l2Normalise(output);
      return output;
    } catch (ex) {
      if (typeof console !== "undefined" && console.error) {
        console.error(`[OnnxSpeakerIdentity] embedding failed: ${ex instanceof Error ? ex.message : String(ex)}`);
      }
      return null;
    }
  }

  /** Build a log-mel tensor of shape [1, NMelBins, NumFrames]. */
  private logMelTensor(window: Float32Array): { data: Float32Array; dims: readonly number[] } {
    const frameSize = Math.trunc((this.config.sampleRateHz * this.config.melFrameMs) / 1000);
    const hopSize = Math.trunc((this.config.sampleRateHz * this.config.melHopMs) / 1000);
    const numFrames = Math.max(1, Math.trunc((window.length - frameSize) / hopSize) + 1);
    const hamming = hammingWindow(frameSize);
    const filters = melFilterbank(this.config.nMelBins, frameSize, this.config.sampleRateHz);

    const data = new Float32Array(1 * this.config.nMelBins * numFrames);
    const frame = new Float32Array(frameSize);
    for (let fi = 0; fi < numFrames; fi++) {
      const start = fi * hopSize;
      for (let i = 0; i < frameSize; i++) {
        frame[i] = (start + i < window.length ? window[start + i] : 0) * hamming[i];
      }
      const power = powerSpectrum(frame);
      for (let m = 0; m < this.config.nMelBins; m++) {
        const filter = filters[m];
        let sum = 0;
        const len = Math.min(power.length, filter.length);
        for (let k = 0; k < len; k++) sum += power[k] * filter[k];
        data[m * numFrames + fi] = Math.fround(Math.log(Math.max(1e-10, sum)));
      }
    }
    return floatTensor(data, [1, this.config.nMelBins, numFrames]);
  }
}

/** Dot product of two equal-length L2-normalised vectors (returns -1 on length mismatch). */
function cosineSimilarity(a: Float32Array, b: readonly number[]): number {
  if (a.length !== b.length) return -1;
  let dot = 0;
  for (let i = 0; i < a.length; i++) dot += a[i] * b[i];
  return dot;
}
