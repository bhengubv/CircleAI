/**
 * voice/onnx_emotion
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/onnx_emotion.ts
//
// OnnxSpeechEmotionDetector.cs — speech-emotion recognition via a wav2vec2-style
// ONNX model (raw float waveform in, class logits out). The ONNX runtime is
// injected behind IOnnxSession. Argmax-softmax picks the label; arousal/valence
// are looked up from the built-in Russell-circumplex map. Ported one-to-one,
// including the default 4-class layout and the max-clip clamp.

import {
  speechEmotionFrame,
  type ISpeechEmotionDetector,
  type SpeechEmotionFrame,
} from "./identity_contracts";
import { readInt16LE, softmaxArgmax } from "./dsp";
import { floatTensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend";

// SUPERB-ER + IEMOCAP standard 4-class layout (kept as default).
const DEFAULT_LABELS: readonly string[] = ["neutral", "happy", "angry", "sad"];

// Russell-circumplex (arousal, valence) coordinates for the standard discrete
// emotion labels. Anything outside the map is (0, 0) = "neutral" in dimensional
// space. Keys are matched case-insensitively (stored lowercase).
const CIRCUMPLEX: ReadonlyMap<string, readonly [number, number]> = new Map([
  ["neutral", [0.0, 0.0]],
  ["happy", [0.55, 0.81]],
  ["happiness", [0.55, 0.81]],
  ["joy", [0.6, 0.82]],
  ["angry", [0.74, -0.62]],
  ["anger", [0.74, -0.62]],
  ["sad", [-0.43, -0.65]],
  ["sadness", [-0.43, -0.65]],
  ["fear", [0.78, -0.64]],
  ["fearful", [0.78, -0.64]],
  ["surprise", [0.85, 0.4]],
  ["surprised", [0.85, 0.4]],
  ["disgust", [0.45, -0.6]],
  ["disgusted", [0.45, -0.6]],
  ["calm", [-0.4, 0.45]],
  ["excited", [0.82, 0.7]],
  ["bored", [-0.65, -0.2]],
  ["frustrated", [0.55, -0.55]],
  ["contempt", [0.2, -0.55]],
] as [string, readonly [number, number]][]);

/** Configuration for {@link OnnxSpeechEmotionDetector}. Mirrors `SpeechEmotionConfig` record. */
export interface SpeechEmotionConfig {
  readonly modelPath: string;
  /** Class labels (defaults to the 4-class SUPERB-ER layout). */
  readonly labels: readonly string[];
  readonly sampleRateHz: number;
  readonly maxClipMs: number;
}

/**
 * Build a {@link SpeechEmotionConfig} with the C# record's defaults
 * (4-class labels, 16 kHz, 8000 ms max clip).
 */
export function speechEmotionConfig(
  modelPath: string,
  overrides: Partial<Omit<SpeechEmotionConfig, "modelPath">> = {},
): SpeechEmotionConfig {
  if (!modelPath || modelPath.trim().length === 0) throw new Error("modelPath is required");
  return {
    modelPath,
    labels: overrides.labels ?? DEFAULT_LABELS,
    sampleRateHz: overrides.sampleRateHz ?? 16_000,
    maxClipMs: overrides.maxClipMs ?? 8_000,
  };
}

export class OnnxSpeechEmotionDetector implements ISpeechEmotionDetector {
  private readonly config: SpeechEmotionConfig;
  private readonly sessionFactory: OnnxSessionFactory;
  private readonly labels: readonly string[];
  private session: IOnnxSession | null = null;
  private disposed = false;

  constructor(config: SpeechEmotionConfig, sessionFactory: OnnxSessionFactory) {
    if (config == null) throw new Error("config is required");
    if (sessionFactory == null) throw new Error("sessionFactory is required");
    this.config = config;
    this.sessionFactory = sessionFactory;
    this.labels = config.labels ?? DEFAULT_LABELS;
  }

  async senseAsync(
    audioPcm16: Uint8Array,
    sampleRateHz: number,
    signal?: AbortSignal,
  ): Promise<SpeechEmotionFrame | null> {
    if (this.disposed) throw new Error("OnnxSpeechEmotionDetector is disposed");
    if (audioPcm16.length === 0) return null;
    if (sampleRateHz !== this.config.sampleRateHz) {
      if (typeof console !== "undefined" && console.error) {
        console.error(
          `[OnnxSpeechEmotionDetector] mismatched sample rate ${sampleRateHz} vs model ${this.config.sampleRateHz}`,
        );
      }
      return null;
    }
    if (signal?.aborted) return null;

    const maxSamples = Math.trunc((sampleRateHz * this.config.maxClipMs) / 1000);
    const nSamples = Math.min(Math.trunc(audioPcm16.length / 2), maxSamples);
    if (nSamples === 0) return null;

    const window = new Float32Array(nSamples);
    for (let i = 0; i < nSamples; i++) window[i] = readInt16LE(audioPcm16, i * 2) / 32768;

    try {
      const session = this.ensureSession();
      const outputs = session.run({ [session.inputName]: floatTensor(window, [1, nSamples]) });
      const logits = outputs[session.outputName].data;

      const [bestIdx, bestProb] = softmaxArgmax(logits);
      const label = (bestIdx >= 0 && bestIdx < this.labels.length ? this.labels[bestIdx] : "unknown").toLowerCase();
      const coords = CIRCUMPLEX.get(label) ?? ([0, 0] as const);
      return speechEmotionFrame(label, coords[0], coords[1], bestProb);
    } catch (ex) {
      if (typeof console !== "undefined" && console.error) {
        console.error(`[OnnxSpeechEmotionDetector] inference failed: ${ex instanceof Error ? ex.message : String(ex)}`);
      }
      return null;
    }
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
}
