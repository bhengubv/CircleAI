// voice/whisper.ts
//
// WhisperTranscriber.cs (backed by whisper.cpp via WhisperInterop.cs P/Invoke).
// The native whisper.cpp dependency is injected behind IWhisperContext /
// WhisperContextFactory — the analogue of the WhisperInterop static bindings +
// whisper_context* handle — so the port is deterministic and needs no native
// library. The transcriber's own logic is ported one-to-one: lazy context load,
// PCM16→float32 conversion, single-shot transcribe, and the accumulate-every-
// ~2 s streaming loop with a final `isFinal` emission.
//
// C# serialises calls on a Lock because whisper.cpp contexts are not
// thread-safe. JS is single-threaded per event loop, so the lock is a no-op;
// the sequential await ordering already matches the C# "one call at a time"
// guarantee.

import {
  partialTranscription,
  throwIfAborted,
  transcriptionResult,
  type IVoiceTranscriber,
  type PartialTranscription,
  type TranscriptionResult,
} from "./contracts.js";
import { decodePcm16ToFloat } from "./dsp.js";

/**
 * The injected whisper.cpp context — the analogue of a loaded
 * `whisper_context*`. `full` runs the pipeline over normalised float32 samples
 * and returns the recognised text + detected BCP-47 language. Implementations
 * wrap the native library; the default is a {@link NullWhisperContext}.
 */
export interface IWhisperContext {
  /**
   * Run the full whisper pipeline over `samples` (mono float32 in [-1, 1] at
   * 16 kHz). Returns the concatenated segment text (already trimmed) and the
   * auto-detected language code, or `null` text on failure.
   */
  full(samples: Float32Array): { text: string; languageCode: string };

  /** Free the context (C# `whisper_free`). */
  free(): void;
}

/** Builds an {@link IWhisperContext} from a GGML model path (C# `whisper_init_from_file`). */
export type WhisperContextFactory = (modelPath: string) => IWhisperContext;

/** No-op {@link IWhisperContext}: recognises nothing (empty "und" result). */
export class NullWhisperContext implements IWhisperContext {
  full(_samples: Float32Array): { text: string; languageCode: string } {
    return { text: "", languageCode: "und" };
  }
  free(): void {
    /* nothing */
  }
}

/**
 * {@link IVoiceTranscriber} backed by an injected whisper.cpp context. Lazy-
 * loads the context on first use and reuses it. Audio input must be PCM 16-bit,
 * 16 kHz, mono (little-endian signed shorts).
 */
export class WhisperTranscriber implements IVoiceTranscriber {
  private readonly modelPath: string;
  private readonly contextFactory: WhisperContextFactory;
  private ctx: IWhisperContext | null = null;
  private disposed = false;

  constructor(modelPath: string, contextFactory: WhisperContextFactory = () => new NullWhisperContext()) {
    if (!modelPath || modelPath.trim().length === 0) throw new Error("modelPath is required");
    this.modelPath = modelPath;
    this.contextFactory = contextFactory;
  }

  async transcribeAsync(pcmAudio: Uint8Array, signal?: AbortSignal): Promise<TranscriptionResult> {
    if (this.disposed) throw new Error("WhisperTranscriber is disposed");
    throwIfAborted(signal);
    return this.transcribeCore(pcmAudio, signal);
  }

  async *streamTranscribeAsync(
    audioChunks: AsyncIterable<Uint8Array>,
    signal?: AbortSignal,
  ): AsyncIterable<PartialTranscription> {
    if (this.disposed) throw new Error("WhisperTranscriber is disposed");
    if (audioChunks == null) throw new Error("audioChunks is required");

    // ~2 seconds of 16 kHz mono 16-bit audio = 64000 bytes.
    const accumulationThresholdBytes = 64_000;

    const parts: Uint8Array[] = [];
    let bufferLength = 0;
    let sinceLastEmit = 0;

    for await (const chunk of audioChunks) {
      throwIfAborted(signal);
      if (chunk.length === 0) continue;

      parts.push(chunk);
      bufferLength += chunk.length;
      sinceLastEmit += chunk.length;

      if (sinceLastEmit >= accumulationThresholdBytes) {
        sinceLastEmit = 0;
        const accumulated = concatAll(parts, bufferLength);
        const result = this.transcribeCore(accumulated, signal);
        yield partialTranscription(result.text, false, result.confidence);
      }
    }

    // Final transcription over the complete buffer.
    if (bufferLength > 0) {
      const finalAudio = concatAll(parts, bufferLength);
      const final = this.transcribeCore(finalAudio, signal);
      yield partialTranscription(final.text, true, final.confidence);
    } else {
      yield partialTranscription("", true, 0);
    }
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    if (this.ctx !== null) {
      this.ctx.free();
      this.ctx = null;
    }
  }

  private ensureContext(): IWhisperContext {
    if (this.ctx !== null) return this.ctx;
    const ctx = this.contextFactory(this.modelPath);
    if (ctx == null) {
      throw new Error(
        `Failed to load whisper model from '${this.modelPath}'. ` +
          "Verify the model file exists and the whisper native binding is available.",
      );
    }
    this.ctx = ctx;
    return this.ctx;
  }

  /** Core transcription: PCM16 → float32 → whisper.full → result. */
  private transcribeCore(pcmAudio: Uint8Array, signal?: AbortSignal): TranscriptionResult {
    throwIfAborted(signal);
    if (pcmAudio.length < 2) return transcriptionResult("", 0, "und");

    const ctx = this.ensureContext();
    // Convert PCM 16-bit signed shorts to float32 in [-1, 1].
    const floats = decodePcm16ToFloat(pcmAudio);

    throwIfAborted(signal);
    const { text, languageCode } = ctx.full(floats);
    const trimmed = (text ?? "").trim();
    // whisper.cpp exposes no per-segment confidence; report 1.0 for non-empty.
    const confidence = trimmed.length === 0 ? 0 : 1;
    return transcriptionResult(trimmed, confidence, languageCode || "und");
  }
}

/** Concatenate accumulated chunks into a single buffer of known total length. */
function concatAll(parts: readonly Uint8Array[], totalLength: number): Uint8Array {
  const out = new Uint8Array(totalLength);
  let offset = 0;
  for (const p of parts) {
    out.set(p, offset);
    offset += p.length;
  }
  return out;
}
