// Rag.kt
//
// Retrieval-augmented context assembly. Kotlin port of CircleAI.Memory (the C#
// reference), mirroring the verified TypeScript pilot (memory/rag.ts) 1:1:
//   • ITextEmbedder — the semantic-ranking seam
//   • RagContextBuilder — retrieves the most relevant episodes and formats them
//     as a compact context block for injection into the B! system prompt
//   • RagPipelineBuilder — fluent factory with sensible defaults
//
// RAG is strictly best-effort: any retrieval / embedding failure degrades to an
// empty string and must never block inference. In-memory port — the C#
// WithSqliteStore convenience is intentionally omitted (no SQLite backend in the
// brain tree); use withStore / withInMemoryStore instead.
//
// Uses the memory-brain EpisodicEntry / IEpisodicStore shape (see
// EpisodicStore.kt): userText / assistantText / appContext / recordedAtUtc plus
// a cosine-search store.

package com.bhengubv.circleai.memory.brain

import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

// ---------------------------------------------------------------------------
// ITextEmbedder
// ---------------------------------------------------------------------------

/** Produces an embedding vector for a text. */
interface ITextEmbedder {
    suspend fun generateAsync(text: String): FloatArray
}

// ---------------------------------------------------------------------------
// RagContextBuilder
// ---------------------------------------------------------------------------

/**
 * Retrieves the most semantically relevant episodes from an [IEpisodicStore]
 * and formats them as a compact context block for injection into the B! system
 * prompt.
 *
 * @param store The episodic store to query.
 * @param embedder Optional embedder. When provided, uses semantic similarity to
 *   rank results; when null, falls back to recency ranking.
 * @param topK Maximum number of episodes to include. Default 5 (floored at 1).
 * @param maxCharsPerEntry Maximum characters taken from each episode's texts.
 *   Default 300 (floored at 50).
 */
class RagContextBuilder(
    private val store: IEpisodicStore,
    private val embedder: ITextEmbedder? = null,
    topK: Int = 5,
    maxCharsPerEntry: Int = 300,
) {
    private val topK: Int = maxOf(1, topK)
    private val maxCharsPerEntry: Int = maxOf(50, maxCharsPerEntry)

    /**
     * Builds a context block for the given [query] text. Returns an empty string
     * when the store is empty or all retrievals fail (RAG is best-effort and
     * must never block inference).
     */
    suspend fun buildContextAsync(query: String?): String {
        if (query == null || query.isBlank()) return ""

        return try {
            var queryEmbedding: FloatArray? = null
            if (embedder != null) {
                try {
                    queryEmbedding = embedder.generateAsync(query)
                } catch (_: Throwable) {
                    // Embedding failure is non-fatal — fall back to recency.
                }
            }

            val entries = store.searchAsync(queryEmbedding, topK)
            if (entries.isEmpty()) "" else formatEntries(entries)
        } catch (_: Throwable) {
            // RAG is strictly best-effort — never break inference.
            ""
        }
    }

    private fun formatEntries(entries: List<EpisodicEntry>): String {
        // Half-budget per side, integer-divided to match the C# `_maxCharsPerEntry / 2`.
        val half = maxCharsPerEntry / 2
        val sb = StringBuilder()
        sb.append("[Relevant past exchanges — for context only]\n")

        for (e in entries) {
            val user = truncate(e.userText, half)
            val asst = truncate(e.assistantText, half)
            val when0 = formatWhen(e) + " UTC"

            sb.append("• [").append(when0).append("] ")
            val appContext = e.appContext
            if (appContext != null && appContext.isNotBlank()) {
                sb.append("(").append(appContext).append(") ")
            }
            sb.append("User: ").append(user).append("\n")
            sb.append("  B!: ").append(asst).append("\n")
        }

        return sb.toString()
    }

    private companion object {
        private val WHEN_FMT: DateTimeFormatter =
            DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm").withZone(ZoneOffset.UTC)

        /** Truncate to [maxLen], replacing the last kept char with an ellipsis (matches C#). */
        fun truncate(text: String?, maxLen: Int): String {
            if (text.isNullOrEmpty()) return ""
            if (text.length <= maxLen) return text
            return text.substring(0, maxLen - 1) + "…"
        }

        /** Formats the entry's timestamp as "yyyy-MM-dd HH:mm" in UTC (matches C# ToString). */
        fun formatWhen(e: EpisodicEntry): String = WHEN_FMT.format(e.recordedAtUtc)
    }
}

// ---------------------------------------------------------------------------
// RagPipelineBuilder
// ---------------------------------------------------------------------------

/**
 * Fluent builder for constructing a [RagContextBuilder] with an episodic store,
 * optional embedder, and tuning parameters.
 *
 * ```
 * val rag = RagPipelineBuilder.create()
 *     .withInMemoryStore()
 *     .withTopK(10)
 *     .withMaxCharsPerEntry(500)
 *     .build()
 * val context = rag.buildContextAsync("user query")
 * ```
 */
class RagPipelineBuilder private constructor() {
    private var store: IEpisodicStore? = null
    private var embedder: ITextEmbedder? = null
    private var topK: Int = 5
    private var maxCharsPerEntry: Int = 300

    /** Sets the episodic memory store to retrieve past exchanges from. */
    fun withStore(store: IEpisodicStore): RagPipelineBuilder {
        this.store = store
        return this
    }

    /**
     * Convenience: creates an [InMemoryEpisodicStore] and uses it. Suitable for
     * tests and short-lived processes where persistence is not needed.
     */
    fun withInMemoryStore(): RagPipelineBuilder {
        this.store = InMemoryEpisodicStore()
        return this
    }

    /**
     * Sets the text embedder for semantic similarity search. When not set, the
     * builder falls back to recency-based retrieval.
     */
    fun withEmbedder(embedder: ITextEmbedder): RagPipelineBuilder {
        this.embedder = embedder
        return this
    }

    /** Sets the max number of relevant past episodes to include. Default 5, min 1. */
    fun withTopK(topK: Int): RagPipelineBuilder {
        require(topK >= 1) { "topK must be at least 1." }
        this.topK = topK
        return this
    }

    /** Sets the max characters taken from each episode's texts. Default 300, min 50. */
    fun withMaxCharsPerEntry(maxChars: Int): RagPipelineBuilder {
        require(maxChars >= 50) { "maxChars must be at least 50." }
        this.maxCharsPerEntry = maxChars
        return this
    }

    /** Builds the [RagContextBuilder] from the accumulated configuration. */
    fun build(): RagContextBuilder {
        val s = store
            ?: throw IllegalStateException(
                "An episodic memory store is required. Call withStore() or " +
                    "withInMemoryStore() before build().",
            )
        return RagContextBuilder(s, embedder, topK, maxCharsPerEntry)
    }

    companion object {
        /** Creates a new [RagPipelineBuilder] instance. */
        fun create(): RagPipelineBuilder = RagPipelineBuilder()
    }
}
