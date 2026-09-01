/**
 * voice/onnx_kws
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/onnx_kws.ts
//
// KwsWakeWordDetector.cs — low-latency ONNX keyword-spotting wake-word detector.
// Runs a tiny KWS CNN on a sliding 1-second window of microphone audio every
// hop (default 100 ms). The ONNX runtime is injected behind IOnnxSession (see
// onnx_backend.ts). Ring-buffer windowing, log-mel tensor construction, softmax,
// the fire-cooldown, and the background listen loop are ported exactly.

import {
  throwIfAborted,
  VoiceOperationCancelledError,
  type IAudioCapture,
  type IWakeWordDetector,
  type WakeWordDetectedEventArgs,
  type WakeWordDetectedHandler,
} from "./contracts";
import { hammingWindow, melFilterbank, powerSpectrum, readInt16LE, softmaxOf } from "./dsp";
import { floatTensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend";

/** Whether the model consumes mel-spectrograms or raw waveform. */
export enum KwsInputKind {
  LogMel = 0,
  RawWaveform = 1,
}

/** Configuration for {@link KwsWakeWordDetector}. Mirrors `KwsConfig` record. */
export interface KwsConfig {
  /** Path to the ONNX model file (passed to the session factory). */
  readonly modelPath: string;
  /** Display phrase surfaced on detection. */
  readonly wakeWord: string;
  /** How the model expects audio. */
  readonly inputKind: KwsInputKind;
  /** Sample rate of incoming audio (must match model training). */
  readonly sampleRateHz: number;
  /** Length of the audio window the model expects, in ms. */
  readonly windowMs: number;
  /** How often to slide the window forward, in ms (latency/CPU trade-off). */
  readonly hopMs: number;
  /** Number of mel bins (for LogMel input). Common: 40, 64, 80. */
  readonly nMelBins: number;
  /** Frame length for mel-spec STFT, in ms. */
  readonly melFrameMs: number;
  /** Hop length for mel-spec STFT, in ms. */
  readonly melHopMs: number;
  /** Output-slot index for the wake-word class. */
  readonly targetClassIndex: number;
  /** Min probability for the target class to fire detection (0..1). */
  readonly threshold: number;
  /** Cooldown between fires, in ms, so one utterance doesn't fire repeatedly. */
  readonly minIntervalBetweenFiresMs: number;
}

/**
 * Build a {@link KwsConfig} with the C# record's defaults
 * (`hey b`, LogMel, 16 kHz, 1000 ms window, 100 ms hop, 40 mel bins,
 * 25/10 ms STFT, class 1, threshold 0.7, 1000 ms cooldown).
 */
export function kwsConfig(modelPath: string, overrides: Partial<Omit<KwsConfig, "modelPath">> = {}): KwsConfig {
  if (!modelPath || modelPath.trim().length === 0) throw new Error("modelPath is required");
  return {
    modelPath,
    wakeWord: overrides.wakeWord ?? "hey b",
    inputKind: overrides.inputKind ?? KwsInputKind.LogMel,
    sampleRateHz: overrides.sampleRateHz ?? 16_000,
    windowMs: overrides.windowMs ?? 1000,
    hopMs: overrides.hopMs ?? 100,
    nMelBins: overrides.nMelBins ?? 40,
    melFrameMs: overrides.melFrameMs ?? 25,
    melHopMs: overrides.melHopMs ?? 10,
    targetClassIndex: overrides.targetClassIndex ?? 1,
    threshold: overrides.threshold ?? 0.7,
    minIntervalBetweenFiresMs: overrides.minIntervalBetweenFiresMs ?? 1000,
  };
}

export class KwsWakeWordDetector implements IWakeWordDetector {
  readonly wakeWord: string;

  private readonly capture: IAudioCapture;
  private readonly config: KwsConfig;
  private readonly sessionFactory: OnnxSessionFactory;
  private readonly handlers = new Set<WakeWordDetectedHandler>();

  private session: IOnnxSession | null = null;
  private listening = false;
  private disposed = false;
  private controller: AbortController | null = null;
  private loop: Promise<void> | null = null;
  private lastFireMs = 0;

  constructor(capture: IAudioCapture, config: KwsConfig, sessionFactory: OnnxSessionFactory) {
    if (capture == null) throw new Error("capture is required");
    if (config == null) throw new Error("config is required");
    if (sessionFactory == null) throw new Error("sessionFactory is required");
    if (config.sampleRateHz <= 0) throw new RangeError("sampleRateHz must be positive");
    if (config.windowMs <= 0) throw new RangeError("windowMs must be positive");
    if (config.hopMs <= 0) throw new RangeError("hopMs must be positive");
    if (config.threshold < 0 || config.threshold > 1) throw new RangeError("threshold must be in [0, 1]");

    this.capture = capture;
    this.config = config;
    this.sessionFactory = sessionFactory;
    this.wakeWord = config.wakeWord;
  }

  get isListening(): boolean {
    return this.listening;
  }

  onWakeWordDetected(handler: WakeWordDetectedHandler): void {
    this.handlers.add(handler);
  }
  offWakeWordDetected(handler: WakeWordDetectedHandler): void {
    this.handlers.delete(handler);
  }

  async startAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("KwsWakeWordDetector is disposed");
    throwIfAborted(signal);
    if (this.listening) return;
    this.controller = new AbortController();
    this.listening = true;
    this.loop = this.listenLoop(this.controller.signal);
  }

  async stopAsync(_signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("KwsWakeWordDetector is disposed");
    if (!this.listening) return;
    this.controller?.abort();
    this.listening = false;
    const toAwait = this.loop;
    if (toAwait != null) {
      try {
        await toAwait;
      } catch (ex) {
        if (!(ex instanceof VoiceOperationCancelledError)) throw ex;
      }
    }
    this.controller = null;
    this.loop = null;
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    try {
      await this.stopAsync();
    } catch (ex) {
      if (!(ex instanceof VoiceOperationCancelledError)) throw ex;
    }
    this.session?.dispose();
    this.session = null;
  }

  private ensureSession(): IOnnxSession {
    if (this.session !== null) return this.session;
    this.session = this.sessionFactory(this.config.modelPath);
    return this.session;
  }

  private raise(args: WakeWordDetectedEventArgs): void {
    for (const h of this.handlers) h(args);
  }

  private async listenLoop(signal: AbortSignal): Promise<void> {
    const windowSamples = Math.trunc((this.config.sampleRateHz * this.config.windowMs) / 1000);
    const hopSamples = Math.trunc((this.config.sampleRateHz * this.config.hopMs) / 1000);
    const ringBuffer = new Float32Array(windowSamples);
    let ringFill = 0;
    let ringWrite = 0;
    let samplesSinceLastInference = 0;
    const minIntervalMs = this.config.minIntervalBetweenFiresMs;

    try {
      for await (const chunkBytes of this.capture.captureAsync(signal)) {
        if (chunkBytes.length === 0) continue;
        throwIfAborted(signal);

        for (let i = 0; i + 1 < chunkBytes.length; i += 2) {
          const s = readInt16LE(chunkBytes, i);
          ringBuffer[ringWrite] = s / 32768;
          ringWrite = (ringWrite + 1) % windowSamples;
          if (ringFill < windowSamples) ringFill++;
          samplesSinceLastInference++;

          if (ringFill < windowSamples) continue;
          if (samplesSinceLastInference < hopSamples) continue;
          samplesSinceLastInference = 0;

          // Linearise the ring into an in-order window for the model.
          const window = new Float32Array(windowSamples);
          const splitAt = windowSamples - ringWrite;
          window.set(ringBuffer.subarray(ringWrite, ringWrite + splitAt), 0);
          window.set(ringBuffer.subarray(0, ringWrite), splitAt);

          const prob = this.predict(window);
          if (prob !== null && prob >= this.config.threshold) {
            const now = Date.now();
            if (now - this.lastFireMs < minIntervalMs) continue;
            this.lastFireMs = now;
            this.raise({ wakeWord: this.wakeWord, detectedAt: new Date(now), confidence: prob });
          }
        }
      }
    } catch (ex) {
      if (ex instanceof VoiceOperationCancelledError || signal.aborted) {
        // Normal shutdown.
      } else if (typeof console !== "undefined" && console.error) {
        console.error(`[KwsWakeWordDetector] loop error: ${ex instanceof Error ? ex.message : String(ex)}`);
      }
    } finally {
      this.listening = false;
    }
  }

  private predict(window: Float32Array): number | null {
    try {
      const session = this.ensureSession();
      const tensor =
        this.config.inputKind === KwsInputKind.RawWaveform
          ? floatTensor(window, [1, window.length])
          : this.logMelTensor(window);
      const outputs = session.run({ [session.inputName]: tensor });
      const logits = outputs[session.outputName].data;
      return softmaxOf(logits, this.config.targetClassIndex);
    } catch (ex) {
      if (typeof console !== "undefined" && console.error) {
        console.error(`[KwsWakeWordDetector] inference failed: ${ex instanceof Error ? ex.message : String(ex)}`);
      }
      return null;
    }
  }

  /** Build a log-mel tensor of shape [1, 1, NMelBins, NumFrames]. */
  private logMelTensor(window: Float32Array): { data: Float32Array; dims: readonly number[] } {
    const frameSize = Math.trunc((this.config.sampleRateHz * this.config.melFrameMs) / 1000);
    const hopSize = Math.trunc((this.config.sampleRateHz * this.config.melHopMs) / 1000);
    const numFrames = Math.max(1, Math.trunc((window.length - frameSize) / hopSize) + 1);

    const hamming = hammingWindow(frameSize);
    const filters = melFilterbank(this.config.nMelBins, frameSize, this.config.sampleRateHz);
    const melBins = this.config.nMelBins;

    const data = new Float32Array(1 * 1 * melBins * numFrames);
    const frame = new Float32Array(frameSize);
    for (let fi = 0; fi < numFrames; fi++) {
      const start = fi * hopSize;
      for (let i = 0; i < frameSize; i++) {
        frame[i] = (start + i < window.length ? window[start + i] : 0) * hamming[i];
      }
      const power = powerSpectrum(frame);
      for (let m = 0; m < melBins; m++) {
        const filter = filters[m];
        let sum = 0;
        const len = Math.min(power.length, filter.length);
        for (let k = 0; k < len; k++) sum += power[k] * filter[k];
        // Row-major index into [1, 1, melBins, numFrames].
        data[m * numFrames + fi] = Math.fround(Math.log(Math.max(1e-10, sum)));
      }
    }
    return floatTensor(data, [1, 1, melBins, numFrames]);
  }
}
