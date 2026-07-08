// inference/generator.ts
//
// DeterministicChatGenerator — a concrete IChatGenerator that stands in for the
// native MNN-backed QwenTextGenerator / KimiVlGenerator (which cannot run
// without libmnnbridge). It is a faithful port of the *contract* those
// generators satisfy:
//
//   • Qwen ChatML prompt building (BuildQwenChatPrompt) — byte-identical.
//   • PowerBudget resolution (RT-11) applied to the per-call token cap.
//   • Prefix-cache consultation (RT-06) keyed on (modelPath, systemPrompt).
//   • <think>…</think> reasoning routing via the shared token router, so
//     streamFragments splits Content vs Reasoning exactly like MNN generators.
//   • Stop-sequence handling with the same default ChatML stops.
//   • generateResponse returns content-only text + reasoningContent, matching
//     QwenTextGenerator.GenerateResponseAsync.
//   • Session save/load markers matching IChatGenerator's default methods.
//   • Vision fallback: when the latest turn carries imageBytes and the model is
//     vision-capable, the image is acknowledged in the deterministic output.
//
// The generated *text* is deterministic (a function of the conversation), so
// tests can assert exact output. This replaces sampling — the whole point of a
// stand-in generator — while preserving every observable behaviour of the
// contract the server + companion layers depend on.

import { ChatFragmentKind, FinishReason } from "../models/index.js";
import type { ChatFragment, ChatMessage, ChatResponse } from "../models/index.js";
import {
  DEFAULT_GENERATION_OPTIONS,
  PowerBudget,
  type GenerationOptions,
  type IChatGenerator,
} from "./index.js";
import { resolvePowerBudget } from "./power_budget.js";
import { PrefixCacheService } from "./prefix_cache.js";
import {
  drainRemainder,
  routeChunk,
  THINK_CLOSE,
  THINK_OPEN,
  TokenRouterSink,
} from "./token_router.js";

const IM_START = "<|im_start|>";
const IM_END = "<|im_end|>";
const END_OF_TEXT = "<|endoftext|>";

/** Default ChatML stop sequences — matches QwenTextGenerator.DefaultStopSequences. */
export const DEFAULT_STOP_SEQUENCES: readonly string[] = [IM_END, IM_START, END_OF_TEXT];

/**
 * Builds a Qwen ChatML prompt. System / user / assistant turns are each wrapped
 * in `<|im_start|>role\n…\n<|im_end|>\n`, and the final assistant turn is left
 * open. Ported verbatim from QwenTextGenerator.BuildQwenChatPrompt.
 */
export function buildQwenChatPrompt(messages: readonly ChatMessage[]): string {
  let sb = "";
  for (const m of messages) {
    const role = !m.role || m.role.trim().length === 0 ? "user" : m.role.trim().toLowerCase();
    sb += IM_START + role + "\n";
    sb += m.content ?? "";
    sb += "\n" + IM_END + "\n";
  }
  sb += IM_START + "assistant\n";
  return sb;
}

/** Extracts the first system-role message content, or null. Matches ExtractSystemPrompt. */
function extractSystemPrompt(messages: readonly ChatMessage[]): string | null {
  for (const m of messages) {
    if (m.role?.toLowerCase() === "system") return m.content;
  }
  return null;
}

/**
 * Configuration for a DeterministicChatGenerator.
 */
export interface DeterministicGeneratorOptions {
  /** Logical model path — used for the prefix-cache key (RT-06). */
  readonly modelPath?: string;
  /** Maximum context window in tokens; caps the per-call token budget. */
  readonly contextSize?: number;
  /** Whether this generator declares vision capability (KimiVl parity). */
  readonly visionCapable?: boolean;
  /** Prefix cache to consult; defaults to PrefixCacheService.default(). */
  readonly prefixCache?: PrefixCacheService;
  /**
   * Whether the model emits a reasoning trace. When true, generateResponse and
   * streamFragments surface a `<think>…</think>` block for the answer.
   */
  readonly emitReasoning?: boolean;
}

/**
 * A concrete, deterministic IChatGenerator. Stand-in for the native
 * QwenTextGenerator / KimiVlGenerator with byte-identical prompt building and
 * fully-faithful reasoning/stop/budget/prefix-cache behaviour.
 */
export class DeterministicChatGenerator implements IChatGenerator {
  private readonly modelPath: string;
  private readonly contextSize: number;
  private readonly prefixCache: PrefixCacheService;
  private readonly emitReasoning: boolean;
  private disposed = false;

  /** true when this generator declares vision capability (KimiVl parity). */
  readonly isVisionCapable: boolean;

  /** Set of prefix-cache keys this generator warmed this process (RT-06). */
  private readonly warmedKeys = new Set<string>();

  constructor(opts: DeterministicGeneratorOptions = {}) {
    this.modelPath = opts.modelPath ?? "deterministic://qwen3";
    this.contextSize = opts.contextSize ?? 4096;
    this.isVisionCapable = opts.visionCapable ?? false;
    this.prefixCache = opts.prefixCache ?? PrefixCacheService.default();
    this.emitReasoning = opts.emitReasoning ?? false;
  }

  async generateAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<string> {
    this.throwIfDisposed();
    let out = "";
    for await (const piece of this.streamAsync(messages, options)) out += piece;
    return out;
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<string> {
    // Content-only: drop reasoning fragments, mirroring StreamAsync.
    for await (const f of this.streamFragmentsAsync(messages, options)) {
      if (f.kind === ChatFragmentKind.Content && f.text.length > 0) yield f.text;
    }
  }

  async *streamFragmentsAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): AsyncGenerator<ChatFragment> {
    this.throwIfDisposed();
    if (!messages) throw new Error("messages required");

    const budget = options?.budget ?? PowerBudget.Normal;
    const requestedMax =
      options?.maxTokens && options.maxTokens > 0 ? options.maxTokens : this.contextSize;
    const resolved = resolvePowerBudget(budget, requestedMax);

    const includeReasoning = options?.includeReasoning ?? DEFAULT_GENERATION_OPTIONS.includeReasoning;
    const stops =
      options?.stopSequences && options.stopSequences.length > 0
        ? options.stopSequences
        : DEFAULT_STOP_SEQUENCES;

    // RT-06: consult the prefix cache before "resetting the model handle".
    if (options?.usePrefixCache) {
      const systemPrompt = extractSystemPrompt(messages);
      const key = PrefixCacheService.keyFor(this.modelPath, systemPrompt);
      if (key !== null) {
        const cached = await this.prefixCache.hasEntry(key);
        if (cached) {
          this.prefixCache.touch(key);
        } else {
          // Populate on first use so subsequent calls with the same
          // (modelPath, systemPrompt) hit the cache.
          await this.prefixCache.writeEntry(key);
          await this.prefixCache.evictIfNeeded();
        }
        this.warmedKeys.add(key);
      }
    }

    // Build the deterministic answer text, then feed it through the same
    // <think> router the native generators use so Content/Reasoning split
    // identically. The token cap bounds the emitted answer length.
    const answer = this.synthesize(messages, resolved.maxTokens);

    // Collect routed fragments synchronously, then yield.
    const collected: ChatFragment[] = [];
    const sink = new TokenRouterSink(stops, (f) => collected.push(f), includeReasoning);
    // Feed in small chunks so the router's holdback logic is exercised exactly
    // like the streaming native path (which delivers per-token fragments).
    for (const chunk of chunkString(answer, 6)) {
      if (routeChunk(sink, chunk)) break;
    }
    drainRemainder(sink);

    for (const f of collected) {
      if (f.text.length > 0) yield f;
    }
  }

  async generateResponse(
    messages: readonly ChatMessage[],
    options?: GenerationOptions,
  ): Promise<ChatResponse> {
    this.throwIfDisposed();
    const started = performance.now();
    let content = "";
    let reasoning = "";
    for await (const f of this.streamFragmentsAsync(messages, options)) {
      if (f.kind === ChatFragmentKind.Reasoning) reasoning += f.text;
      else content += f.text;
    }
    const latencyMs = performance.now() - started;

    return {
      text: content,
      tokensIn: 0, // native does not surface a per-call prompt token count yet
      tokensOut: 0, // streaming count not aggregated; bridge estimates
      latencyMs,
      finishReason: FinishReason.Stop,
      reasoningContent: reasoning.length === 0 ? null : reasoning,
    };
  }

  async saveSessionAsync(path: string): Promise<boolean> {
    if (!path || path.trim().length === 0) throw new Error("path required");
    this.throwIfDisposed();
    // Deterministic marker round-trip — mirrors IChatGenerator's default.
    const marker =
      `circleai-session-marker\n` +
      `type:DeterministicChatGenerator\n` +
      `saved_utc:${new Date().toISOString()}\n`;
    await this.prefixCache.writeRaw(path, marker);
    return true;
  }

  async loadSessionAsync(path: string): Promise<boolean> {
    if (!path || path.trim().length === 0) throw new Error("path required");
    this.throwIfDisposed();
    const text = await this.prefixCache.readRaw(path);
    return text !== null && text.startsWith("circleai-session-marker");
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.warmedKeys.clear();
  }

  // ── Internals ─────────────────────────────────────────────────────────────

  /**
   * Deterministic answer synthesis. Produces a stable, conversation-derived
   * reply. When emitReasoning is on, a `<think>…</think>` block precedes the
   * answer so the reasoning router has real content to split. The token cap
   * (from the resolved PowerBudget) bounds the answer to `maxTokens` words.
   */
  private synthesize(messages: readonly ChatMessage[], maxTokens: number): string {
    const lastUser = [...messages].reverse().find((m) => m.role?.toLowerCase() === "user");
    const userText = (lastUser?.content ?? "").trim();
    const hasImage = messages.some((m) => m.imageBytes && m.imageBytes.length > 0);

    let body: string;
    if (this.isVisionCapable && hasImage) {
      body = userText.length > 0 ? `I see the image. Regarding "${userText}", here is my reply.` : "I see the image you shared.";
    } else if (userText.length > 0) {
      body = `You said: ${userText}`;
    } else {
      body = "Hello.";
    }

    // Word-cap the body to the resolved token budget (1 word ~ 1 token here).
    const cap = Math.max(1, maxTokens);
    const words = body.split(" ");
    if (words.length > cap) body = words.slice(0, cap).join(" ");

    if (this.emitReasoning) {
      const think = `The user's intent is clear; I will answer directly.`;
      return `${THINK_OPEN}${think}${THINK_CLOSE}${body}`;
    }
    return body;
  }

  private throwIfDisposed(): void {
    if (this.disposed) throw new Error("DeterministicChatGenerator is disposed");
  }
}

/** Split a string into fixed-size chunks (last may be shorter). */
function chunkString(s: string, size: number): string[] {
  if (s.length === 0) return [];
  const out: string[] = [];
  for (let i = 0; i < s.length; i += size) out.push(s.substring(i, i + size));
  return out;
}
