/**
 * memory/llm_extractor
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// memory/llm_extractor.ts
// LLM-backed knowledge-graph extraction: turn → (subject, predicate, object)
// triples. Ported from CircleAI.Companion (LlmKnowledgeGraphExtractor) — the C#
// reference.
//
// Uses an on-device IChatGenerator to ask an LLM to extract triples from a
// single conversation turn. The extraction prompt asks for strict-JSON output;
// the parser is defensive against the model emitting extra prose or fences.

import type { IKnowledgeGraphExtractor } from "./extractor";
import type { KnowledgeTriple } from "./graph";
import type { IChatGenerator } from "../inference/index";

/** Confidence used when the model omits (or malforms) the "c" field. */
const DEFAULT_CONFIDENCE = 0.75;

const SYSTEM_PROMPT =
  "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. " +
  "Identify entities (people, places, things, concepts) and facts. " +
  'Output a single JSON array of triples like [{"s":"Subject","p":"predicate","o":"object","c":0.0-1.0}, ...]. ' +
  "Only output the JSON — no prose, no markdown fences.";

/** Model-backed extractor: asks an LLM for triples and parses its JSON reply. */
export class LlmKnowledgeGraphExtractor implements IKnowledgeGraphExtractor {
  private readonly ai: IChatGenerator;

  constructor(ai: IChatGenerator) {
    if (!ai) throw new Error("ai required");
    this.ai = ai;
  }

  async extractFromTurnAsync(
    userText: string,
    assistantText: string,
    sourceEpisodeId: string | null,
  ): Promise<readonly KnowledgeTriple[]> {
    if (isBlank(userText) && isBlank(assistantText)) return [];

    const userMsg =
      "USER:\n" + (userText ?? "") + "\nASSISTANT:\n" + (assistantText ?? "") + "\n";

    let reply: string;
    try {
      reply = await this.ai.generateAsync([
        { role: "system", content: SYSTEM_PROMPT },
        { role: "user", content: userMsg },
      ]);
    } catch {
      // LLM call failed — degrade gracefully, no triples this turn.
      return [];
    }

    return parseTriples(reply, sourceEpisodeId);
  }
}

/**
 * Parses the model's reply into triples. Finds the first `[` and last `]`,
 * JSON-parses the slice, and reads s/p/o/c from each object. Any structural
 * problem yields an empty list rather than throwing.
 */
export function parseTriples(
  raw: string,
  sourceEpisodeId: string | null,
): readonly KnowledgeTriple[] {
  if (isBlank(raw)) return [];
  const firstBracket = raw.indexOf("[");
  const lastBracket = raw.lastIndexOf("]");
  if (firstBracket < 0 || lastBracket <= firstBracket) return [];
  const jsonSlice = raw.slice(firstBracket, lastBracket + 1);

  try {
    const parsed: Object = JSON.parse(jsonSlice);
    if (!Array.isArray(parsed)) return [];

    const now = new Date();
    const hits: KnowledgeTriple[] = [];
    for (const entry of parsed) {
      if (entry == null || typeof entry !== "object" || Array.isArray(entry)) continue;
      const obj = entry as Map<string, unknown>;
      const s = typeof obj.s === "string" ? obj.s : null;
      const p = typeof obj.p === "string" ? obj.p : null;
      const o = typeof obj.o === "string" ? obj.o : null;
      const c =
        typeof obj.c === "number" && Number.isFinite(obj.c)
          ? clamp(obj.c, 0, 1)
          : DEFAULT_CONFIDENCE;
      if (isBlank(s) || isBlank(p) || isBlank(o)) continue;
      hits.push({
        subject: s!,
        predicate: p!,
        object: o!,
        source: sourceEpisodeId,
        confidence: c,
        recordedAtUtc: now,
      });
    }
    return hits;
  } catch {
    // Malformed JSON — return nothing.
    return [];
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}

function clamp(x: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, x));
}
