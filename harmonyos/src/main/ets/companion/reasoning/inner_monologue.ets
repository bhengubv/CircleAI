/**
 * companion/reasoning/inner_monologue
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/inner_monologue.ts
//
// Two IInnerMonologue implementations, ported from the C# reference:
//
//   TemplateInnerMonologue      — HerJarvisRealImplementations.cs #13. A
//                                 narrative-template reflection: summarise the
//                                 context, infer a direction, and fill one of
//                                 three frames.
//   ReasoningLoopInnerMonologue — ReasoningLoopInnerMonologue.cs. An o1 /
//                                 DeepSeek-R1 style loop: drives an
//                                 IChatGenerator's fragment stream and keeps
//                                 the reasoning fragments as the thought.
//
// Both in-memory and deterministic (given a deterministic generator).

import type { IInnerMonologue, SelfReflection } from "./contracts";
import type { ChatMessage, GenerationOptions, IChatGenerator } from "../../inference/index";
import { ChatFragmentKind, streamFragmentsAsync } from "../../inference/index";

// =====================================================================
// 13. TemplateInnerMonologue — narrative-template reflection over context.
// =====================================================================

const FRAMES: readonly string[] = [
  "Observation: {summary}. Implication: this likely means {direction}.",
  "Looking at {summary}, the salient pattern is {direction}.",
  "Given {summary}, my next step is to {direction}.",
];

/**
 * Fills one of three narrative frames from a summary of the context and an
 * inferred direction. The frame is chosen by a deterministic hash of the
 * context so the same context always yields the same frame.
 *
 * NOTE on the frame selector: the C# reference uses
 * `unchecked(contextJson.GetHashCode() & int.MaxValue) % Frames.Length`.
 * .NET's `string.GetHashCode()` is randomised per-process (has been since
 * .NET Core), so its value is NOT reproducible across runs or languages —
 * it cannot be byte-matched. The faithful port of the INTENT ("pick a frame
 * deterministically from the content") is a stable string hash. We use a
 * 32-bit FNV-1a masked to non-negative, then `% FRAMES.length`, which gives a
 * deterministic, content-derived frame in TypeScript.
 */
export class TemplateInnerMonologue implements IInnerMonologue {
  async reflectAsync(contextJson: string): Promise<SelfReflection> {
    if (contextJson == null) throw new Error("contextJson required");
    const summary = summarise(contextJson);
    const direction = inferDirection(contextJson);
    const seed = stableHash(contextJson) & 0x7fffffff; // & int.MaxValue
    const frame = FRAMES[seed % FRAMES.length];
    const thought = frame.replace("{summary}", summary).replace("{direction}", direction);
    return { thought, at: new Date() };
  }
}

/**
 * C# `Summarise`: strip JSON punctuation to spaces, split on space
 * (dropping empties), take the first 12 tokens, re-join with single spaces.
 */
function summarise(json: string): string {
  const clean = json.replace(/[{}[\]"]/g, " ");
  const words = clean.split(" ").filter((w) => w.length > 0).slice(0, 12);
  return words.join(" ");
}

/**
 * C# `InferDirection`: first case-insensitive substring hit wins, in order
 * error → goal → user, else the default.
 */
function inferDirection(json: string): string {
  const lower = json.toLowerCase();
  if (lower.includes("error")) return "diagnose the failure first";
  if (lower.includes("goal")) return "advance toward the stated goal";
  if (lower.includes("user")) return "respond to the user";
  return "gather more context";
}

/**
 * Deterministic 32-bit FNV-1a hash of a string (unsigned). Replaces the
 * non-reproducible `string.GetHashCode()` used only to pick a frame index.
 */
function stableHash(s: string): number {
  let h = 0x811c9dc5; // FNV offset basis
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i) & 0xff;
    // FNV prime 16777619, kept in 32-bit range via Math.imul.
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0; // to unsigned 32-bit
}

// =====================================================================
// ReasoningLoopInnerMonologue — inner monologue via a reasoning-capable LLM.
// =====================================================================

const REASONING_SYSTEM_PROMPT =
  "You are this user's inner monologue. Reason carefully before responding. " +
  "Use <think>...</think> blocks for chain-of-thought. The visible answer " +
  "afterwards should be short and reflective — not a solution, an observation.";

/**
 * Drives an IChatGenerator's fragment stream and captures the Reasoning-kind
 * fragments as the inner monologue, with the Content fragments forming the
 * externally-visible conclusion. Prefers the reasoning trace as the "thought";
 * falls back to visible content, then to "(no inner state)".
 *
 * Mirrors the C# `_llm.StreamFragmentsAsync(...)` — which is a DEFAULT
 * interface method that wraps `StreamAsync` and tags every chunk Content when
 * a generator does not surface reasoning. The TypeScript equivalent of that
 * default method is the free `streamFragmentsAsync(generator, ...)` helper in
 * the inference module, so we call it here for identical fallback behaviour.
 */
export class ReasoningLoopInnerMonologue implements IInnerMonologue {
  private readonly llm: IChatGenerator;

  constructor(llm: IChatGenerator) {
    if (!llm) throw new Error("llm required");
    this.llm = llm;
  }

  async reflectAsync(contextJson: string): Promise<SelfReflection> {
    if (contextJson == null) throw new Error("contextJson required");

    const messages: ChatMessage[] = [
      { role: "system", content: REASONING_SYSTEM_PROMPT },
      {
        role: "user",
        content: `Context (raw JSON):\n${contextJson}\n\nReflect on this in 2-3 sentences.`,
      },
    ];
    const options: GenerationOptions = {
      maxTokens: 256,
      // C# `Temperature = 0.5f` — the one genuine `float` site in this work
      // unit. Math.fround at the float boundary per convention (0.5 is exactly
      // representable, so this is value-preserving).
      temperature: Math.fround(0.5),
      includeReasoning: true,
    };

    let reasoning = "";
    let content = "";
    try {
      for await (const frag of streamFragmentsAsync(this.llm, messages, options)) {
        if (frag.kind === ChatFragmentKind.Reasoning) reasoning += frag.text;
        else content += frag.text;
      }
    } catch (ex) {
      // C# swallows and logs to Debug; we mirror that (no throw).
      // eslint-disable-next-line no-console
      if (typeof console !== "undefined" && console.debug) {
        console.debug(
          `[ReasoningLoopInnerMonologue] LLM stream failed: ${
            ex instanceof Error ? ex.message : String(ex)
          }`,
        );
      }
    }

    // Prefer the reasoning trace as the "thought"; fall back to visible content.
    let thought = reasoning.length > 0 ? reasoning.trim() : content.trim();
    if (thought.length === 0) thought = "(no inner state)";
    return { thought, at: new Date() };
  }
}
