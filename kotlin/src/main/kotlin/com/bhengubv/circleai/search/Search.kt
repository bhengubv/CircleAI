// Search.kt
//
// Kotlin port of CircleAI.Search (SearchPrimitives.cs + VectorSearch.cs +
// SimdOps.cs) — the C# reference is the EXACT spec. Shared search-relevance
// helpers (tokenisation + BM25-style term-frequency scoring) plus two
// cosine-similarity vector-math helpers.
//
// Fidelity notes:
//   * C# `static class` -> Kotlin `object`.
//   * C# `ReadOnlySpan<float>` -> `FloatArray`.
//   * The C# SIMD fast-path (System.Numerics.Vector) is a JIT-level micro-opt
//     that produces the same numeric result as the scalar loop; the Kotlin port
//     keeps the scalar reduction, matching the C# fallback path exactly.
//   * `VectorMath` and `SimdOps` are two separate C# global-namespace types with
//     the same body; both are ported here under the `search` package.

package com.bhengubv.circleai.search

import kotlin.math.sqrt

// =====================================================================
// SearchTokenisation (SearchPrimitives.cs)
// =====================================================================

/** Query / document tokenisation helpers. Mirrors C# `SearchTokenisation`. */
object SearchTokenisation {
    private val DELIMS = charArrayOf(' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')', '[', ']', '"', '\'')

    /** Splits [text] on punctuation/whitespace, lowercases, and drops empties. */
    fun tokenise(text: String): List<String> =
        text.split(*DELIMS)
            .map { it.trim().lowercase() }
            .filter { it.isNotEmpty() }
}

// =====================================================================
// SearchScoring (SearchPrimitives.cs)
// =====================================================================

/** BM25-style relevance scoring helpers. Mirrors C# `SearchScoring`. */
object SearchScoring {
    /** Fraction of [docTokens] equal (ordinal) to [term]. */
    fun termFrequency(term: String, docTokens: List<String>): Double {
        if (docTokens.isEmpty()) return 0.0
        var c = 0
        for (t in docTokens) if (t == term) c++
        return c.toDouble() / docTokens.size
    }

    /** Sum of per-query-term term-frequencies over [docTokens]. */
    fun simpleRelevance(queryTokens: List<String>, docTokens: List<String>): Double {
        if (queryTokens.isEmpty() || docTokens.isEmpty()) return 0.0
        var score = 0.0
        for (q in queryTokens) score += termFrequency(q, docTokens)
        return score
    }
}

// =====================================================================
// VectorMath (VectorSearch.cs)
// =====================================================================

/** Cosine-similarity vector math. Mirrors C# `VectorMath`. */
object VectorMath {
    /** Cosine similarity of two equal, non-zero-length vectors. */
    fun cosineSimilarity(a: FloatArray, b: FloatArray): Float {
        require(a.size == b.size && a.isNotEmpty()) { "Vectors must be same non-zero length" }
        return calculateCosineFallback(a, b)
    }

    private fun calculateCosineFallback(a: FloatArray, b: FloatArray): Float {
        var dot = 0f
        var normA = 0f
        var normB = 0f
        for (i in a.indices) {
            dot += a[i] * b[i]
            normA += a[i] * a[i]
            normB += b[i] * b[i]
        }
        return dot / (sqrt(normA) * sqrt(normB))
    }
}

// =====================================================================
// SimdOps (SimdOps.cs)
// =====================================================================

/** Cosine-similarity vector math (SIMD-shaped in C#). Mirrors C# `SimdOps`. */
object SimdOps {
    /** Cosine similarity of two equal, non-zero-length vectors. */
    fun cosineSimilarity(a: FloatArray, b: FloatArray): Float {
        require(a.size == b.size && a.isNotEmpty()) { "Vectors must be the same non-zero length." }
        var dot = 0f
        var normA = 0f
        var normB = 0f
        for (i in a.indices) {
            dot += a[i] * b[i]
            normA += a[i] * a[i]
            normB += b[i] * b[i]
        }
        return dot / (sqrt(normA) * sqrt(normB))
    }
}
