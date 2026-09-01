/**
 * memory/rag
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// memory/rag.ts
// Retrieval-augmented context assembly. Ported from CircleAI.Memory (C#):
//   • ITextEmbedder (CircleAI.Embeddings) — the semantic-ranking seam
//   • RagContextBuilder — retrieves the most relevant episodes and formats them
//     as a compact context block for injection into the B! system prompt
//   • RagPipelineBuilder — fluent factory with sensible defaults
//
// RAG is strictly best-effort: Object retrieval / embedding failure degrades to an
// empty string and must never block inference. In-memory port — the C#
// WithSqliteStore convenience is intentionally omitted (no SQLite backend in the
// TS tree); use withStore / withInMemoryStore instead.

import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from "./index";
import { InMemoryEpisodicStore } from "./stores";

// ─────────────────────────────────────────────────────────────────────────────
// ITextEmbedder — CircleAI.Embeddings.ITextEmbedder
// ─────────────────────────────────────────────────────────────────────────────

/** Produces an embedding vector for a text. */
export interface ITextEmbedder {
  generateAsync(text: string): Promise<number[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// RagContextBuilder — CircleAI.Memory.RagContextBuilder
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Retrieves the most semantically relevant episodes from an
 * {@link IEpisodicMemoryStore} and formats them as a compact context block for
 * injection into the B! system prompt.
 */
export class RagContextBuilder {
  private readonly store: IEpisodicMemoryStore;
  private readonly embedder: ITextEmbedder | null;
  private readonly topK: number;
  private readonly maxCharsPerEntry: number;

  /**
   * @param store The episodic store to query.
   * @param embedder Optional embedder. When provided, uses semantic similarity to
   *   rank results; when null, falls back to recency ranking.
   * @param topK Maximum number of episodes to include. Default 5 (floored at 1).
   * @param maxCharsPerEntry Maximum characters taken from each episode's texts.
   *   Default 300 (floored at 50).
   */
  constructor(
    store: IEpisodicMemoryStore,
    embedder: ITextEmbedder | null = null,
    topK = 5,
    maxCharsPerEntry = 300,
  ) {
    if (!store) throw new Error("store required");
    this.store = store;
    this.embedder = embedder;
    this.topK = Math.max(1, topK);
    this.maxCharsPerEntry = Math.max(50, maxCharsPerEntry);
  }

  /**
   * Builds a context block for the given `query` text. Returns an empty string
   * when the store is empty or all retrievals fail (RAG is best-effort and must
   * never block inference).
   */
  async buildContextAsync(query: string): Promise<string> {
    if (query == null || query.trim().length === 0) return "";

    try {
      let queryEmbedding: number[] | null = null;
      if (this.embedder !== null) {
        try {
          queryEmbedding = await this.embedder.generateAsync(query);
        } catch {
          // Embedding failure is non-fatal — fall back to recency.
        }
      }

      const entries = await this.store.searchAsync(queryEmbedding, this.topK);
      if (entries.length === 0) return "";

      return this.formatEntries(entries);
    } catch {
      // RAG is strictly best-effort — never break inference.
      return "";
    }
  }

  private formatEntries(entries: readonly EpisodicMemoryEntry[]): string {
    // Half-budget per side, integer-divided to match the C# `_maxCharsPerEntry / 2`.
    const half = Math.trunc(this.maxCharsPerEntry / 2);
    let sb = "[Relevant past exchanges — for context only]\n";

    for (const e of entries) {
      const user = truncate(e.userText, half);
      const asst = truncate(e.assistantText, half);
      const when = formatWhen(e.recordedAtUtc) + " UTC";

      sb += "• [" + when + "] ";
      if (e.appContext != null && e.appContext.trim().length > 0)
        sb += "(" + e.appContext + ") ";
      sb += "User: " + user + "\n";
      sb += "  B!: " + asst + "\n";
    }

    return sb;
  }
}

/** Truncate to maxLen, replacing the last kept char with an ellipsis (matches C#). */
function truncate(text: string, maxLen: number): string {
  if (text == null || text.length === 0) return "";
  if (text.length <= maxLen) return text;
  return text.substring(0, maxLen - 1) + "…";
}

/** Formats a Date as "yyyy-MM-dd HH:mm" in UTC (matches C# ToString on a UTC value). */
function formatWhen(d: Date): string {
  const y = d.getUTCFullYear().toString().padStart(4, "0");
  const mo = (d.getUTCMonth() + 1).toString().padStart(2, "0");
  const day = d.getUTCDate().toString().padStart(2, "0");
  const h = d.getUTCHours().toString().padStart(2, "0");
  const mi = d.getUTCMinutes().toString().padStart(2, "0");
  return `${y}-${mo}-${day} ${h}:${mi}`;
}

// ─────────────────────────────────────────────────────────────────────────────
// RagPipelineBuilder — CircleAI.Memory.RagPipelineBuilder
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Fluent builder for constructing a {@link RagContextBuilder} with an episodic
 * store, optional embedder, and tuning parameters.
 *
 * @example
 * const rag = RagPipelineBuilder.create()
 *   .withInMemoryStore()
 *   .withTopK(10)
 *   .withMaxCharsPerEntry(500)
 *   .build();
 * const context = await rag.buildContextAsync("user query");
 */
export class RagPipelineBuilder {
  private store: IEpisodicMemoryStore | null = null;
  private embedder: ITextEmbedder | null = null;
  private topK = 5;
  private maxCharsPerEntry = 300;

  private constructor() {}

  /** Creates a new {@link RagPipelineBuilder} instance. */
  static create(): RagPipelineBuilder {
    return new RagPipelineBuilder();
  }

  /** Sets the episodic memory store to retrieve past exchanges from. */
  withStore(store: IEpisodicMemoryStore): this {
    if (!store) throw new Error("store required");
    this.store = store;
    return this;
  }

  /**
   * Convenience: creates an {@link InMemoryEpisodicStore} and uses it. Suitable
   * for tests and short-lived processes where persistence is not needed.
   */
  withInMemoryStore(): this {
    this.store = new InMemoryEpisodicStore();
    return this;
  }

  /**
   * Sets the text embedder for semantic similarity search. When not set, the
   * builder falls back to recency-based retrieval.
   */
  withEmbedder(embedder: ITextEmbedder): this {
    if (!embedder) throw new Error("embedder required");
    this.embedder = embedder;
    return this;
  }

  /** Sets the max number of relevant past episodes to include. Default 5, min 1. */
  withTopK(topK: number): this {
    if (topK < 1) throw new RangeError("topK must be at least 1.");
    this.topK = topK;
    return this;
  }

  /** Sets the max characters taken from each episode's texts. Default 300, min 50. */
  withMaxCharsPerEntry(maxChars: number): this {
    if (maxChars < 50) throw new RangeError("maxChars must be at least 50.");
    this.maxCharsPerEntry = maxChars;
    return this;
  }

  /** Builds the {@link RagContextBuilder} from the accumulated configuration. */
  build(): RagContextBuilder {
    if (this.store === null)
      throw new Error(
        "An episodic memory store is required. Call withStore() or " +
          "withInMemoryStore() before build().",
      );
    return new RagContextBuilder(this.store, this.embedder, this.topK, this.maxCharsPerEntry);
  }
}
