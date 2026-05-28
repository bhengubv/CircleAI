import { ChatMessage } from "../models/index.js";
export type { ChatMessage };
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
export declare const DEFAULT_GENERATION_OPTIONS: Required<Omit<GenerationOptions, "seed" | "stopSequences">>;
/**
 * Contract for an on-device chat-style text generator.
 * Implementations own native model state and must be disposed.
 */
export interface IChatGenerator {
    /**
     * Generates a complete assistant reply for the given conversation.
     */
    generateAsync(messages: readonly ChatMessage[], options?: GenerationOptions): Promise<string>;
    /**
     * Streams the assistant reply token-by-token (or piece-by-piece) as
     * it is decoded. Each yielded string is the next chunk to append to
     * the output — callers should concatenate them in order.
     */
    streamAsync(messages: readonly ChatMessage[], options?: GenerationOptions): AsyncGenerator<string>;
    /** Dispose native resources held by this generator. */
    dispose(): void;
}
