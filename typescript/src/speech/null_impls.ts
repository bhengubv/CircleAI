// speech/null_impls.ts
//
// Fail-closed defaults for the ASR / TTS / wake-word / OCR Speech contracts
// (port of CircleAI.Speech.NullImplementations.cs). Lets hosting layers wire the
// Speech pack optionally; absence of a real backend degrades to deterministic
// empty answers.

import {
  speechTranscriptionResult,
  synthesisResult,
  ocrResult,
  type ISpeechRecognizer,
  type ISpeechSynthesizer,
  type ISpeechWakeWordDetector,
  type IOpticalCharacterRecognizer,
  type OcrResult,
  type SpeechTranscriptionResult,
  type SynthesisResult,
  type WakeWordHandler,
} from "./contracts.js";

const EMPTY = new Uint8Array(0);

export class NullSpeechRecognizer implements ISpeechRecognizer {
  static readonly instance = new NullSpeechRecognizer();
  get backendId(): string {
    return "null";
  }

  transcribeAsync(
    _audioPcm16Mono: Uint8Array,
    _sampleRateHz: number,
    languageHint: string | null = null,
    _signal?: AbortSignal,
  ): Promise<SpeechTranscriptionResult> {
    return Promise.resolve(speechTranscriptionResult("", languageHint, [], 0));
  }
}

export class NullSpeechSynthesizer implements ISpeechSynthesizer {
  static readonly instance = new NullSpeechSynthesizer();
  get backendId(): string {
    return "null";
  }

  synthesizeAsync(
    _text: string,
    _voiceId: string | null = null,
    _languageHint: string | null = null,
    _signal?: AbortSignal,
  ): Promise<SynthesisResult> {
    return Promise.resolve(synthesisResult(EMPTY, 16_000, 0));
  }
}

export class NullSpeechWakeWordDetector implements ISpeechWakeWordDetector {
  get backendId(): string {
    return "null";
  }

  subscribe(_handler: WakeWordHandler): { dispose(): void } {
    return { dispose(): void {} };
  }

  startAsync(_signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  stopAsync(_signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }

  disposeAsync(): Promise<void> {
    return Promise.resolve();
  }
}

export class NullOpticalCharacterRecognizer implements IOpticalCharacterRecognizer {
  static readonly instance = new NullOpticalCharacterRecognizer();
  get backendId(): string {
    return "null";
  }

  recognizeAsync(
    _imageBytes: Uint8Array,
    _languageHint: string | null = "auto",
    _signal?: AbortSignal,
  ): Promise<OcrResult> {
    return Promise.resolve(ocrResult("", []));
  }
}
