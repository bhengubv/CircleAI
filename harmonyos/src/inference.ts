// inference.ts
//
// Contract for an on-device chat-style text generator — ArkTS port.
// Implementations own native model state.

import type { ChatMessage } from './models';
import { ChatFragmentKind, type ChatFragment } from './models_v15';

export type { ChatFragment, ChatMessage };
export { ChatFragmentKind };

/** Knobs for a single generation call. */
export interface GenerationOptions {
  /** Maximum number of new tokens to produce. Default 512. */
  readonly maxTokens?:     number;
  /** Sampling temperature. 0 = greedy; higher = more random. Default 0.7. */
  readonly temperature?:   number;
  /** Nucleus sampling cutoff (top-p). 1.0 disables. Default 0.9. */
  readonly topP?:          number;
  /** Top-k cutoff. 0 disables. Default 40. */
  readonly topK?:          number;
  /** Optional RNG seed. null means non-deterministic. */
  readonly seed?:          number | null;
  /**
   * Optional substrings that will end generation when matched in the
   * emitted output.
   */
  readonly stopSequences?: readonly string[];
  /**
   * Whether to surface the model's reasoning trace (Qwen3
   * `<think>…</think>`) on the call. Default `true`.
   *
   * When `true` the generator separates reasoning from the final answer:
   * `ChatResponse.reasoningContent` gets the reasoning, `ChatResponse.text`
   * gets the answer. Streaming callers see fragments tagged with
   * `ChatFragmentKind.Reasoning`.
   *
   * When `false` the generator still RUNS reasoning (this is per-call output
   * gating, NOT a thinking disable) but the reasoning text is dropped — only
   * the final answer reaches the caller.
   */
  readonly includeReasoning?: boolean;
}

/**
 * Contract for an on-device chat-style text generator.
 */
export abstract class IChatGenerator {
  abstract generate(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string>;

  /**
   * Streams the assistant reply chunk-by-chunk. Content only — any reasoning
   * inside `<think>…</think>` is filtered out. Use `streamFragments` when
   * you also need the reasoning stream.
   */
  abstract stream(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string, void, unknown>;

  /**
   * Fragment-aware streaming variant. Yields each piece tagged as either
   * Content or Reasoning so the caller can route the model's `<think>` block
   * into a separate `reasoning_content` field (o1 / DeepSeek style).
   *
   * Default implementation wraps `stream` and tags every chunk as Content;
   * generators that surface reasoning override this method.
   */
  async *streamFragments(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<ChatFragment, void, unknown> {
    for await (const chunk of this.stream(messages, options)) {
      yield { kind: ChatFragmentKind.Content, text: chunk };
    }
  }

  abstract dispose(): void;
}
