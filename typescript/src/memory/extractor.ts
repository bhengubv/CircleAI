// memory/extractor.ts
// Knowledge-graph extraction: turn → (subject, predicate, object) triples.
// Ported from Circle.AI.Companion (IKnowledgeGraphExtractor,
// HeuristicKnowledgeGraphExtractor) — the C# reference.
//
// The heuristic extractor is model-free: it links the content words a turn
// mentions to the memory they came from, two-way, so a later question can reach
// an older memory across turns. It is the offline counterpart to the LLM-based
// extractor (same interface, no network) — the graph still fills, just coarsely.

import type { KnowledgeTriple } from "./graph.js";

/** Turns a conversation turn into knowledge-graph triples. */
export interface IKnowledgeGraphExtractor {
  extractFromTurnAsync(
    userText: string,
    assistantText: string,
    sourceEpisodeId: string | null,
  ): Promise<readonly KnowledgeTriple[]>;
}

const DEFAULT_CONFIDENCE = 0.6;

// Common function words carry no association — drop them so links form on
// meaningful words (names, places, symptoms, things), not "the" and "my".
const STOP = new Set<string>([
  "the", "a", "an", "and", "or", "but", "if", "is", "are", "was", "were", "be", "been", "being",
  "to", "of", "in", "on", "at", "for", "with", "from", "by", "as", "into", "about", "over", "under",
  "my", "your", "our", "their", "his", "her", "its", "this", "that", "these", "those",
  "i", "you", "he", "she", "it", "we", "they", "me", "him", "them", "us",
  "do", "does", "did", "done", "have", "has", "had", "will", "would", "can", "could", "should",
  "shall", "may", "might", "must", "not", "no", "yes", "so", "than", "then", "there", "here",
  "how", "why", "what", "when", "where", "who", "which", "whom",
  "am", "get", "got", "really", "just", "very", "much", "many", "some", "any", "all",
]);

/** Model-free extractor: links a turn's content words to their memory, two-way. */
export class HeuristicKnowledgeGraphExtractor implements IKnowledgeGraphExtractor {
  async extractFromTurnAsync(
    userText: string,
    assistantText: string,
    sourceEpisodeId: string | null,
  ): Promise<readonly KnowledgeTriple[]> {
    // The memory node is identified by the source id when given, else the user's
    // words — so recall can hand back the memory it came from.
    const memory =
      sourceEpisodeId && sourceEpisodeId.trim().length > 0 ? sourceEpisodeId : userText;
    if (!memory || memory.trim().length === 0) return [];

    const words = contentWords((userText ?? "") + " " + (assistantText ?? ""));
    const now = new Date();
    const triples: KnowledgeTriple[] = [];
    for (const w of words) {
      // Two-way so a walk can go word → memory → word → memory across turns.
      triples.push({ subject: memory, predicate: "mentions", object: w, source: sourceEpisodeId, confidence: DEFAULT_CONFIDENCE, recordedAtUtc: now });
      triples.push({ subject: w, predicate: "seenin", object: memory, source: sourceEpisodeId, confidence: DEFAULT_CONFIDENCE, recordedAtUtc: now });
    }
    return triples;
  }
}

/** Lowercase, split on separators, drop short/stop words, dedupe preserving order. */
function contentWords(text: string): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const raw of text.toLowerCase().split(/[ \t\n\r.,?!;:'"()/-]+/)) {
    if (raw.length < 3 || STOP.has(raw)) continue;
    if (!seen.has(raw)) {
      seen.add(raw);
      result.push(raw);
    }
  }
  return result;
}
