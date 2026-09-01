/**
 * voice/onnx_tts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/onnx_tts.ts
//
// OnnxTtsEngine.cs — an ITtsEngine backed by a VITS/Kokoro-style ONNX model.
// The ONNX runtime is injected behind IOnnxSession/OnnxSessionFactory (see
// onnx_backend.ts) so the port needs no native library; the session is lazily
// built on first synthesis and reused. Character-level tokenisation, VITS scale
// tensors, sentence splitting for streaming, and float→PCM16 conversion are all
// ported exactly.

import {
  throwIfAborted,
  ttsSynthesisResult,
  type ITtsEngine,
  type TtsSynthesisResult,
} from "./contracts";
import { floatTensor, int64Tensor, type IOnnxSession, type OnnxSessionFactory } from "./onnx_backend";

// Standard VITS-style input/output tensor names.
const INPUT_NAME = "input";
const INPUT_LENGTHS_NAME = "input_lengths";
const SCALES_NAME = "scales";

const fr = Math.fround;

/**
 * {@link ITtsEngine} that runs a VITS/Kokoro-style ONNX model. The model
 * accepts text token IDs and produces a float waveform, converted to 16-bit
 * signed PCM at the configured sample rate.
 */
export class OnnxTtsEngine implements ITtsEngine {
  private readonly modelPath: string;
  private readonly sampleRate: number;
  private readonly sessionFactory: OnnxSessionFactory;
  private session: IOnnxSession | null = null;
  private disposed = false;

  /**
   * @param modelPath path to the ONNX model file (passed to the factory).
   * @param sessionFactory builds the injected ONNX session from the path.
   * @param sampleRate output sample rate in Hz (default 24000; must match the model).
   */
  constructor(modelPath: string, sessionFactory: OnnxSessionFactory, sampleRate = 24_000) {
    if (!modelPath || modelPath.trim().length === 0) throw new Error("modelPath is required");
    if (sessionFactory == null) throw new Error("sessionFactory is required");
    if (sampleRate <= 0) throw new RangeError("sampleRate must be positive");
    this.modelPath = modelPath;
    this.sessionFactory = sessionFactory;
    this.sampleRate = sampleRate;
  }

  async synthesiseAsync(text: string, signal?: AbortSignal): Promise<TtsSynthesisResult> {
    throwIfAborted(signal);
    if (this.disposed) throw new Error("OnnxTtsEngine is disposed");

    if (!text || text.trim().length === 0) {
      return ttsSynthesisResult(new Uint8Array(0), this.sampleRate, 1, 16);
    }
    return this.synthesiseCore(text, signal);
  }

  async *streamSynthesiseAsync(text: string, signal?: AbortSignal): AsyncIterable<Uint8Array> {
    if (this.disposed) throw new Error("OnnxTtsEngine is disposed");
    if (!text || text.trim().length === 0) return;

    for (const sentence of splitSentences(text)) {
      throwIfAborted(signal);
      if (!sentence || sentence.trim().length === 0) continue;

      const result = this.synthesiseCore(sentence.trim(), signal);
      if (result.audioData.length > 0) {
        yield result.audioData;
      }
    }
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.session?.dispose();
    this.session = null;
  }

  /** Ensure the ONNX session is built (lazy, reused). */
  private ensureSession(): IOnnxSession {
    if (this.session !== null) return this.session;
    this.session = this.sessionFactory(this.modelPath);
    return this.session;
  }

  /** Core synthesis: tokenise → infer → convert waveform to PCM16. */
  private synthesiseCore(text: string, signal?: AbortSignal): TtsSynthesisResult {
    throwIfAborted(signal);

    const session = this.ensureSession();
    const tokens = tokeniseText(text);
    if (tokens.length === 0) {
      return ttsSynthesisResult(new Uint8Array(0), this.sampleRate, 1, 16);
    }

    // Input: token IDs as int64 [1, sequence_length].
    const inputData = new BigInt64Array(tokens.length);
    for (let i = 0; i < tokens.length; i++) inputData[i] = BigInt(tokens[i]);
    const inputTensor = int64Tensor(inputData, [1, tokens.length]);

    // Input lengths: [1] containing the sequence length.
    const inputLengths = int64Tensor(BigInt64Array.of(BigInt(tokens.length)), [1]);

    // Scales: [3] — noise_scale, length_scale, noise_scale_w.
    const scales = floatTensor(Float32Array.of(fr(0.667), fr(1.0), fr(0.8)), [3]);

    throwIfAborted(signal);
    const outputs = session.run({
      [INPUT_NAME]: inputTensor,
      [INPUT_LENGTHS_NAME]: inputLengths,
      [SCALES_NAME]: scales,
    });
    const waveform = outputs[session.outputName].data;

    return ttsSynthesisResult(floatWaveformToPcm16(waveform), this.sampleRate, 1, 16);
  }
}

/**
 * Character-level tokenisation fallback. Produces
 * `[BOS/blank, char+1, blank, char+1, blank, ...]` — blank = 0, char tokens are
 * the code point + 1. Exposed for parity with the C# `internal` helper.
 */
export function tokeniseText(text: string): number[] {
  if (!text || text.length === 0) return [];
  const result: number[] = [0]; // BOS / blank
  for (let i = 0; i < text.length; i++) {
    result.push(text.charCodeAt(i) + 1); // character token
    result.push(0); // inter-character blank
  }
  return result;
}

/** Convert a float waveform (values in [-1, 1]) to little-endian PCM16 bytes. */
function floatWaveformToPcm16(waveform: Float32Array): Uint8Array {
  const pcm = new Uint8Array(waveform.length * 2);
  for (let i = 0; i < waveform.length; i++) {
    // Clamp to [-1, 1] then scale to short range.
    const sample = Math.max(-1, Math.min(1, waveform[i]));
    let value = Math.trunc(fr(sample * fr(32767)));
    if (value < -32768) value = -32768;
    if (value > 32767) value = 32767;
    if (value < 0) value += 0x10000;
    pcm[i * 2] = value & 0xff;
    pcm[i * 2 + 1] = (value >> 8) & 0xff;
  }
  return pcm;
}

/** Split text into sentences on `. ! ? ;`, keeping each delimiter. */
function splitSentences(text: string): string[] {
  const sentences: string[] = [];
  let start = 0;
  const delimiters = new Set([".", "!", "?", ";"]);
  for (let i = 0; i < text.length; i++) {
    if (delimiters.has(text[i])) {
      const sentence = text.slice(start, i + 1);
      if (sentence.trim().length > 0) sentences.push(sentence);
      start = i + 1;
    }
  }
  if (start < text.length) {
    const remainder = text.slice(start);
    if (remainder.trim().length > 0) sentences.push(remainder);
  }
  return sentences;
}
