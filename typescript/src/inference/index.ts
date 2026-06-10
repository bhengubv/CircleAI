// inference/index.ts
// On-device text generation contracts.
// Ported from CircleAI.Inference (C#) at version 1.5.0.

import {
  ChatMessage,
  ChatResponse,
  FinishReason,
} from "../models/index.js";

// Re-export so callers can import from inference if they prefer.
export type { ChatMessage, ChatResponse };
export { FinishReason };

// ─────────────────────────────────────────────────────────────────────────────
// GenerationOptions
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Knobs for a single generation call.
 */
export interface GenerationOptions {
  /** Maximum number of new tokens to produce. Default 512. */
  readonly maxTokens?: number;
  /** Sampling temperature. 0 = greedy; higher = more random. Default 0.7. */
  readonly temperature?: number;
  /** Nucleus sampling cutoff (top-p). 1.0 disables. Default 0.9. */
  readonly topP?: number;
  /** Top-k cutoff. 0 disables. Default 40. */
  readonly topK?: number;
  /** Optional RNG seed. null means non-deterministic. */
  readonly seed?: number;
  /**
   * Optional substrings that will end generation when matched in the
   * emitted output (e.g. role-tag boundaries).
   */
  readonly stopSequences?: string[];
}

/** Default generation options, matching C# defaults. */
export const DEFAULT_GENERATION_OPTIONS: Required<
  Omit<GenerationOptions, "seed" | "stopSequences">
> = {
  maxTokens: 512,
  temperature: 0.7,
  topP: 0.9,
  topK: 40,
};

// ─────────────────────────────────────────────────────────────────────────────
// IChatGenerator
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Contract for an on-device chat-style text generator.
 * Implementations own native model state and must be disposed.
 */
export interface IChatGenerator {
  /**
   * Generates a complete assistant reply for the given conversation.
   */
  generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string>;

  /**
   * Streams the assistant reply token-by-token (or piece-by-piece) as
   * it is decoded. Each yielded string is the next chunk to append to
   * the output — callers should concatenate them in order.
   */
  streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string>;

  /** Dispose native resources held by this generator. */
  dispose(): void;
}

// ─────────────────────────────────────────────────────────────────────────────
// generateResponseAsync — Protocol equivalent of C# default-method
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Wraps IChatGenerator.generateAsync into a structured ChatResponse.
 *
 * TypeScript interfaces don't carry default methods the way C# default
 * interface methods do, so this is exposed as a free helper. Native
 * generators may shadow it with a method that reports exact token counts
 * from the inference engine — this default approximates.
 */
export async function generateResponseAsync(
  generator: IChatGenerator,
  messages: readonly ChatMessage[],
  options?: GenerationOptions,
): Promise<ChatResponse> {
  const started = performance.now();
  const text = await generator.generateAsync(messages, options);
  const latencyMs = performance.now() - started;

  return {
    text,
    tokensIn: approxTokensMessages(messages),
    tokensOut: approxTokens(text),
    latencyMs,
    finishReason: FinishReason.Stop,
  };
}

function approxTokens(text: string | undefined): number {
  if (!text) return 0;
  // Crude 4-chars-per-token approximation; matches the C# fallback.
  return Math.max(1, Math.floor(text.length / 4));
}

function approxTokensMessages(messages: readonly ChatMessage[]): number {
  return messages.reduce((acc, m) => acc + approxTokens(m.content), 0);
}

// ─────────────────────────────────────────────────────────────────────────────
// ChatCapability flags + IModelSelector
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Capabilities a chat model declares. Mirrors CircleAI.Inference.ChatCapability.
 * Use as a bit-flag set (e.g. `Tools | Vision`).
 */
export const ChatCapability = {
  None: 0,
  Default: 1,
  Tools: 2,
  Vision: 4,
  LongContext: 8,
  Reasoning: 16,
} as const;
export type ChatCapability = (typeof ChatCapability)[keyof typeof ChatCapability];

/** Convenience: OR-combine two capability values. */
export function capabilityOr(a: number, b: number): number {
  return a | b;
}

/** Convenience: check if `set` contains every flag in `required`. */
export function capabilityHas(set: number, required: number): boolean {
  return (set & required) === required;
}

// DeviceTier + DeviceProbe live in device/. Cross-module import here.
import type { DeviceProbe, DeviceTier } from "../device/index.js";

/** Re-export for callers who want to import everything from inference. */
export type { DeviceProbe, DeviceTier };

/** One selector result. `tier` is the device tier the pick was sized for. */
export interface ModelSelection {
  readonly modelId: string;
  readonly requiresDownload: boolean;
  readonly estimatedBytes: number;
  readonly tier: DeviceTier;
}

/**
 * Picks a model that fits the device + the requested capabilities.
 * Mirrors CircleAI.Inference.IModelSelector.
 */
export interface IModelSelector {
  /**
   * Returns the highest-quality entry that satisfies every flag in
   * `required` AND has minRamGb <= probe RAM AND minStorageGb <= free.
   */
  bestFit(probe: DeviceProbe, required: number): ModelSelection;

  /** Every selection candidate in registry order — diagnostics use. */
  allCandidates(probe: DeviceProbe): readonly ModelSelection[];
}
