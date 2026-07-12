// languages/translation/index.ts
//
// Full-parity port of CircleAI.Languages.Translation (C#). C# is the exact spec.
//
//   • TranslationTypes.cs → TranslationMode, TranslationRequest,
//     TranslationResult, ConversationTurn.
//   • ITranslationEngine.cs / ILiveTranslator.cs → the engine + live-translator
//     contracts.
//   • LlmTranslationEngine.cs → on-device LLM translator with streaming and
//     bidirectional live conversation, over the injected IChatGenerator seam.
//
// All processing is on-device — the engine only talks to the injected
// IChatGenerator (../../inference), so there are no network calls and no data
// leaves the device, matching the C# guarantee.

import type { ChatMessage } from "../../models/index.js";
import type { GenerationOptions, IChatGenerator } from "../../inference/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// TranslationTypes.cs
// ─────────────────────────────────────────────────────────────────────────────

/** Register / domain the translation should honour. Mirrors TranslationMode. */
export enum TranslationMode {
  Standard = "Standard",
  Conversational = "Conversational",
  Document = "Document",
  Technical = "Technical",
  Legal = "Legal",
  Medical = "Medical",
}

/** A request to translate a piece of text between two languages. */
export interface TranslationRequest {
  readonly text: string;
  readonly sourceBcpTag: string;
  readonly targetBcpTag: string;
  readonly mode: TranslationMode;
  readonly contextHint: string | null;
}

/** Build a {@link TranslationRequest} with the C# record's default arguments. */
export function makeTranslationRequest(
  text: string,
  sourceBcpTag: string,
  targetBcpTag: string,
  mode: TranslationMode = TranslationMode.Standard,
  contextHint: string | null = null,
): TranslationRequest {
  return { text, sourceBcpTag, targetBcpTag, mode, contextHint };
}

/** Result of a completed translation. */
export interface TranslationResult {
  readonly originalText: string;
  readonly translatedText: string;
  readonly sourceBcpTag: string;
  readonly targetBcpTag: string;
  readonly confidence: number;
  readonly translatedAt: Date;
}

/** One turn in a live bidirectional conversation. */
export interface ConversationTurn {
  readonly speakerBcpTag: string;
  readonly originalText: string;
  readonly translatedText: string | null;
  readonly timestamp: Date;
}

// ─────────────────────────────────────────────────────────────────────────────
// ITranslationEngine.cs / ILiveTranslator.cs
// ─────────────────────────────────────────────────────────────────────────────

/**
 * On-device translation engine. No network call, no data leaving the device.
 * Translates meaning — not just words — using the on-device LLM.
 */
export interface ITranslationEngine {
  translateAsync(request: TranslationRequest, signal?: AbortSignal): Promise<TranslationResult>;

  streamTranslateAsync(request: TranslationRequest, signal?: AbortSignal): AsyncGenerator<string>;

  isLanguagePairSupportedAsync(
    sourceBcpTag: string,
    targetBcpTag: string,
    signal?: AbortSignal,
  ): Promise<boolean>;
}

/**
 * Bidirectional live conversation translator. Party A speaks `partyABcpTag`;
 * party B speaks `partyBBcpTag`. Each turn is translated in real time so both
 * parties hear each other. Runs entirely on-device.
 */
export interface ILiveTranslator extends ITranslationEngine {
  streamConversationAsync(
    inputStream: AsyncIterable<ConversationTurn>,
    partyABcpTag: string,
    partyBBcpTag: string,
    signal?: AbortSignal,
  ): AsyncGenerator<ConversationTurn>;
}

// ─────────────────────────────────────────────────────────────────────────────
// LlmTranslationEngine.cs
// ─────────────────────────────────────────────────────────────────────────────

/**
 * {@link ILiveTranslator} backed by the on-device LLM via an injected
 * {@link IChatGenerator}. All processing is on-device — no API calls, no data
 * leaving the device. Faithful port of CircleAI.Languages.Translation.
 * LlmTranslationEngine.
 */
export class LlmTranslationEngine implements ILiveTranslator {
  private readonly generator: IChatGenerator;

  constructor(generator: IChatGenerator) {
    if (generator == null) throw new Error("generator required");
    this.generator = generator;
  }

  async translateAsync(
    request: TranslationRequest,
    signal?: AbortSignal,
  ): Promise<TranslationResult> {
    const messages: ChatMessage[] = [{ role: "user", content: buildPrompt(request) }];
    const translated = await this.generator.generateAsync(messages, optionsFor(signal));

    return {
      originalText: request.text,
      translatedText: translated.trim(),
      sourceBcpTag: request.sourceBcpTag,
      targetBcpTag: request.targetBcpTag,
      confidence: Math.fround(0.9),
      translatedAt: new Date(),
    };
  }

  async *streamTranslateAsync(
    request: TranslationRequest,
    signal?: AbortSignal,
  ): AsyncGenerator<string> {
    const messages: ChatMessage[] = [{ role: "user", content: buildPrompt(request) }];
    for await (const token of this.generator.streamAsync(messages, optionsFor(signal))) {
      throwIfAborted(signal);
      yield token;
    }
  }

  // On-device LLM handles any pair it was trained on.
  // eslint-disable-next-line @typescript-eslint/require-await
  async isLanguagePairSupportedAsync(
    _sourceBcpTag: string,
    _targetBcpTag: string,
    _signal?: AbortSignal,
  ): Promise<boolean> {
    return true;
  }

  async *streamConversationAsync(
    inputStream: AsyncIterable<ConversationTurn>,
    partyABcpTag: string,
    partyBBcpTag: string,
    signal?: AbortSignal,
  ): AsyncGenerator<ConversationTurn> {
    for await (const turn of inputStream) {
      throwIfAborted(signal);
      const targetTag = turn.speakerBcpTag === partyABcpTag ? partyBBcpTag : partyABcpTag;

      const req = makeTranslationRequest(
        turn.originalText,
        turn.speakerBcpTag,
        targetTag,
        TranslationMode.Conversational,
      );

      const result = await this.translateAsync(req, signal);

      // `turn with { TranslatedText = result.TranslatedText }`.
      yield { ...turn, translatedText: result.translatedText };
    }
  }
}

/** Byte-identical port of LlmTranslationEngine.BuildPrompt. */
function buildPrompt(r: TranslationRequest): string {
  return (
    `Translate the following text from ${r.sourceBcpTag} to ${r.targetBcpTag}. ` +
    `Mode: ${r.mode}. Preserve meaning and cultural context, not just literal words. ` +
    (r.contextHint != null ? `Context: ${r.contextHint}. ` : "") +
    `Return only the translation with no explanation.\n\n${r.text}`
  );
}

/** The C# calls pass only the cancellation token; map an abort signal via stopSequences-free options. */
function optionsFor(_signal?: AbortSignal): GenerationOptions | undefined {
  // The C# GenerateAsync/StreamAsync are invoked with default options + the
  // token; IChatGenerator here has no per-call token, so cancellation is
  // enforced by the caller's abort checks. No option overrides are needed.
  return undefined;
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new Error("Operation cancelled");
}
