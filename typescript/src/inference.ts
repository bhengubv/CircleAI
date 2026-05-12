// inference.ts
//
// Contract for an on-device chat-style text generator.
// Implementations own native model state.

import type { ChatMessage } from './models';

export type { ChatMessage };

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
   * emitted output (e.g. role-tag boundaries).
   */
  readonly stopSequences?: readonly string[];
}

/**
 * Contract for an on-device chat-style text generator.
 * Implementations own native model state and must be disposed.
 */
export abstract class IChatGenerator {
  /**
   * Generates a complete assistant reply for the given conversation.
   */
  abstract generate(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string>;

  /**
   * Streams the assistant reply token-by-token (or piece-by-piece) as it is decoded.
   * Each yielded string is the next chunk to append to the output — callers should
   * concatenate them in order.
   */
  abstract stream(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string, void, unknown>;

  /** Dispose the generator and release model resources. */
  abstract dispose(): void;
}
