// Recall.kt
//
// Fused associative recall (Reciprocal Rank Fusion). Kotlin port of
// Circle.AI.Companion (IRecall, FusedRecall) — the C# reference — mirroring the
// TypeScript pilot (memory/recall.ts) and Go port (memory_recall.go) 1:1.
//
// Fuses two memory systems with incomparable score spaces — episodic cosine
// similarity and graph association (Personalised PageRank) — into one ranked
// context. RRF combines ranked lists by *position*, so it needs no shared score
// scale: each source contributes 1 / (k + rank).
//
// Cold-start is automatic: a new user has an empty graph, so only episodic
// contributes and the fused order equals the episodic order — no special case.

package com.bhengubv.circleai.memory.brain

import java.time.format.DateTimeFormatter

/** Unified memory recall — the most relevant memories for a turn. */
interface IRecall {
    /**
     * Recall the [topK] most relevant memories for the current turn. [query] drives
     * graph association; [queryEmbedding] drives episodic cosine similarity (may be
     * null → episodic recency fallback).
     */
    suspend fun recallAsync(
        query: String,
        queryEmbedding: FloatArray?,
        topK: Int = 5,
    ): List<MemoryHit>
}

/** Tuning for [FusedRecall]. */
data class FusedRecallOptions(
    /** Candidates pulled from each source before fusion. Default 20. */
    val candidatePoolSize: Int = 20,
    /** RRF damping constant k. Default 60 (the standard value). */
    val rrfK: Int = 60,
    /**
     * Graph hits whose backing confidence (metadata key "confidence") is below this
     * are dropped. Applied only when a hit actually carries a confidence value.
     * Default 0.4.
     */
    val graphConfidenceThreshold: Float = 0.4f,
)

/** Reciprocal-Rank-Fusion recall over episodic similarity + graph association. */
class FusedRecall(
    private val episodic: IEpisodicStore,
    private val graph: IHippoRagStore? = null,
    private val opts: FusedRecallOptions = FusedRecallOptions(),
) : IRecall {

    override suspend fun recallAsync(
        query: String,
        queryEmbedding: FloatArray?,
        topK: Int,
    ): List<MemoryHit> {
        require(topK > 0) { "topK must be positive" }

        val pool = opts.candidatePoolSize

        // Fast path: episodic similarity (or recency when the embedding is null).
        val episodicHits = episodic.searchAsync(queryEmbedding, pool)

        // Slow path: graph association. Optional and best-effort — a missing, empty,
        // or failing graph degrades to pure episodic, never throws. An empty query
        // cannot seed a graph walk, so skip it.
        var graphHits: List<MemoryHit> = emptyList()
        if (graph != null && query.isNotBlank()) {
            graphHits = try {
                graph.multiHopRecallAsync(query, pool)
            } catch (ex: Exception) {
                emptyList()
            }
        }

        // Reciprocal Rank Fusion: accumulate 1 / (k + rank) per candidate across both
        // ranked lists, keyed by normalised text so a memory surfaced by both sources
        // reinforces rather than duplicates. LinkedHashMap preserves first-seen order so
        // the stable sort below keeps episodic order on ties (cold-start == episodic).
        val k = opts.rrfK
        val fused = LinkedHashMap<String, FusedCandidate>()

        fun accumulate(item: MemoryItem, oneBasedRank: Int) {
            val key = normaliseKey(item.text)
            if (key.isEmpty()) return
            val contribution = 1.0 / (k + oneBasedRank)
            val existing = fused[key]
            if (existing != null) existing.score += contribution
            else fused[key] = FusedCandidate(item, contribution)
        }

        for (i in episodicHits.indices) accumulate(adaptEpisodic(episodicHits[i]), i + 1)

        for (i in graphHits.indices) {
            if (isBelowConfidence(graphHits[i])) continue
            accumulate(graphHits[i].item, i + 1)
        }

        return fused.values
            .sortedByDescending { it.score }
            .take(topK)
            .map { MemoryHit(it.item, it.score.toFloat()) }
    }

    private fun isBelowConfidence(hit: MemoryHit): Boolean {
        val meta = hit.item.metadata ?: return false
        val raw = meta["confidence"] ?: return false
        val c = raw.toFloatOrNull() ?: return false
        return c < opts.graphConfidenceThreshold
    }

    private class FusedCandidate(val item: MemoryItem, var score: Double)

    private companion object {
        private val ISO = DateTimeFormatter.ISO_INSTANT

        fun adaptEpisodic(e: EpisodicEntry): MemoryItem {
            val meta = LinkedHashMap<String, String>()
            meta["source"] = "episodic"
            meta["recordedAt"] = ISO.format(e.recordedAtUtc)
            if (e.assistantText.isNotEmpty()) meta["assistantText"] = e.assistantText
            if (!e.appContext.isNullOrEmpty()) meta["appContext"] = e.appContext
            return MemoryItem(e.id, e.userText, meta)
        }

        /** Lowercase + collapse internal whitespace so equivalent texts fuse to one key. */
        fun normaliseKey(text: String?): String {
            if (text.isNullOrBlank()) return ""
            val sb = StringBuilder(text.length)
            var prevSpace = false
            for (ch in text.trim()) {
                if (ch.isWhitespace()) {
                    if (!prevSpace) {
                        sb.append(' ')
                        prevSpace = true
                    }
                } else {
                    sb.append(ch.lowercaseChar())
                    prevSpace = false
                }
            }
            return sb.toString()
        }
    }
}
