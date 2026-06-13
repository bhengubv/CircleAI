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
   */
  readonly includeReasoning?: boolean;
  /**
   * (RT-11) Declarative power budget for this call. Default
   * `PowerBudget.Normal` auto-downgrades to `Low` below 15% battery.
   */
  readonly budget?: PowerBudget;
  /**
   * (RT-06) Whether the runtime should consult the cross-session prefix
   * cache. Default `false`.
   */
  readonly usePrefixCache?: boolean;
}

/** Per-call power budget. Mirrors CircleAI.Inference.PowerBudget. */
export enum PowerBudget {
  /** Opt out — honour maxTokens literally. */
  None   = 0,
  /** ~64 token cap; prefers TQ4 KV; smaller model in chain. */
  Low    = 1,
  /** Default. ~512 token cap. Auto-downgrades to Low below 15% battery. */
  Normal = 2,
  /** ~2048 token cap; full FP16 KV. Auto-throttles on thermal warnings. */
  High   = 3,
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

  /**
   * (RT-02) Save the current model session to `path`. Returns `true` on
   * success. Default implementation returns `false`; native generators
   * (MNN-backed) override.
   */
  async saveSessionAsync(_path: string): Promise<boolean> { return false; }

  /**
   * (RT-02) Load a previously-saved session from `path`. Returns `true` on
   * success. Default implementation returns `false`.
   */
  async loadSessionAsync(_path: string): Promise<boolean> { return false; }

  abstract dispose(): void;
}
