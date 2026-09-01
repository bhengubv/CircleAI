/**
 * search/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// search/index.ts
//
// Full-parity port of CircleAI.Search (C#). C# is the exact spec.
//
//   • SearchTokenisation — query/document tokenisation (SearchPrimitives.cs).
//   • SearchScoring      — term-frequency + simple relevance (SearchPrimitives.cs).
//   • VectorMath         — cosine similarity (VectorSearch.cs).
//   • SimdOps            — cosine similarity (SimdOps.cs).
//
// NOTE ON SIMD: the C# VectorMath/SimdOps have a hardware-accelerated fast path
// and a scalar fallback that compute the SAME cosine value. The SIMD path is an
// unobservable performance detail; TS ports the scalar math, which is the
// observable contract. Math.fround is applied at every C# `float` site so the
// single-precision accumulation matches.
//
// Type mappings (C# → TS):
//   ReadOnlySpan<float> / float[]     → number[]  (Math.fround at float sites)
//   IReadOnlyList<string>             → readonly string[]
//   MathF.Sqrt                        → Math.fround(Math.sqrt(...))
//   double                            → number (no fround — C# double is f64)

// ─────────────────────────────────────────────────────────────────────────────
// SearchTokenisation — mirrors C# `SearchTokenisation` (CircleAI.Search).
// ─────────────────────────────────────────────────────────────────────────────

/** Query/document tokenisation helpers. Mirrors C# `SearchTokenisation`. */
export class SearchTokenisation {
  private constructor() {
    /* static-only */
  }

  /**
   * Splits `text` on whitespace + punctuation, lower-cases (invariant), and
   * drops empty tokens. Mirrors C# `SearchTokenisation.Tokenise`.
   */
  static tokenise(text: string): readonly string[] {
    if (text == null) throw new Error("text required");
    // C# separators: ' ' '\n' '\r' '\t' ',' '.' ';' ':' '(' ')' '[' ']' '"' '\''
    return text
      .split(/[ \n\r\t,.;:()[\]"']/)
      .map((t) => t.trim().toLowerCase())
      .filter((t) => t.length > 0);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SearchScoring — mirrors C# `SearchScoring` (CircleAI.Search).
// ─────────────────────────────────────────────────────────────────────────────

/** BM25-style relevance helpers. Mirrors C# `SearchScoring`. */
export class SearchScoring {
  private constructor() {
    /* static-only */
  }

  /**
   * Fraction of `docTokens` equal (ordinal) to `term`. Returns 0 for an empty
   * document. Mirrors C# `SearchScoring.TermFrequency`.
   */
  static termFrequency(term: string, docTokens: readonly string[]): number {
    if (docTokens == null) throw new Error("docTokens required");
    if (docTokens.length === 0) return 0;
    let c = 0;
    for (const t of docTokens) if (t === term) c++;
    return c / docTokens.length;
  }

  /**
   * Sum of per-query-term term-frequencies. Returns 0 when either side is
   * empty. Mirrors C# `SearchScoring.SimpleRelevance`.
   */
  static simpleRelevance(
    queryTokens: readonly string[],
    docTokens: readonly string[],
  ): number {
    if (queryTokens == null) throw new Error("queryTokens required");
    if (docTokens == null) throw new Error("docTokens required");
    if (queryTokens.length === 0 || docTokens.length === 0) return 0;
    let score = 0;
    for (const q of queryTokens) score += SearchScoring.termFrequency(q, docTokens);
    return score;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// VectorMath — mirrors C# top-level static `VectorMath` (VectorSearch.cs).
// ─────────────────────────────────────────────────────────────────────────────

/** Cosine-similarity vector math. Mirrors C# `VectorMath`. */
export class VectorMath {
  private constructor() {
    /* static-only */
  }

  /**
   * Cosine similarity of two equal-length non-zero vectors. Throws otherwise.
   * Single-precision accumulation (Math.fround) matches the C# `float` maths.
   * Mirrors C# `VectorMath.CosineSimilarity`.
   */
  static cosineSimilarity(a: number[], b: number[]): number {
    if (a.length !== b.length || a.length === 0)
      throw new Error("Vectors must be same non-zero length");
    return cosineFloat(a, b);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SimdOps — mirrors C# top-level static `SimdOps` (SimdOps.cs).
// ─────────────────────────────────────────────────────────────────────────────

/** SIMD cosine-similarity operations. Mirrors C# `SimdOps`. */
export class SimdOps {
  private constructor() {
    /* static-only */
  }

  /**
   * Cosine similarity of two equal-length non-zero vectors. Throws otherwise.
   * Mirrors C# `SimdOps.CosineSimilarity` (scalar-equivalent path).
   */
  static cosineSimilarity(a: number[], b: number[]): number {
    if (a.length !== b.length || a.length === 0)
      throw new Error("Vectors must be the same non-zero length.");
    return cosineFloat(a, b);
  }
}

// ── shared single-precision cosine (the C# scalar fallback body) ──────────────

function cosineFloat(a: number[], b: number[]): number {
  let dot = Math.fround(0);
  let normA = Math.fround(0);
  let normB = Math.fround(0);
  for (let i = 0; i < a.length; i++) {
    dot = Math.fround(dot + Math.fround(a[i] * b[i]));
    normA = Math.fround(normA + Math.fround(a[i] * a[i]));
    normB = Math.fround(normB + Math.fround(b[i] * b[i]));
  }
  // MathF.Sqrt → single-precision sqrt; the division stays single-precision.
  return Math.fround(dot / Math.fround(Math.fround(fsqrt(normA)) * Math.fround(fsqrt(normB))));
}

/** MathF.Sqrt — single-precision square root. */
function fsqrt(x: number): number {
  return Math.fround(Math.sqrt(x));
}
