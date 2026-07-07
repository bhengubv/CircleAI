// EpisodicStore.kt
//
// Concrete in-memory episodic store for the memory-brain. Kotlin port of
// Circle.AI.Memory (InMemoryEpisodicStore) — the C# reference — mirroring the
// TypeScript pilot (memory/stores.ts) and Go port (memory_stores.go) 1:1.
//
// NOTE — a NEW store, deliberately separate from com.bhengubv.circleai.memory's
// existing EpisodicMemoryEntry / IEpisodicMemoryStore. That pre-existing pair is
// a persona-layer type (content/createdUtc, save/getRecent/delete, no cosine
// search). The memory-brain needs the C#/TS/Go reference shape: a
// user↔assistant exchange (userText/assistantText/appContext/recordedAtUtc) and
// a store that exposes cosine search + recency fallback + FIFO cap + prune. The
// two coexist rather than one being bent to the other.
//
// All data is lost when the process exits; a persistent (SQLite) backend is a
// later slice. cosine == dot product because both vectors are L2-normalised at
// write time.

package com.bhengubv.circleai.memory.brain

import java.time.Instant

// ---------------------------------------------------------------------------
// EpisodicEntry
// ---------------------------------------------------------------------------

/**
 * A single recorded episode (one user↔assistant exchange) stored in
 * [IEpisodicStore]. [embedding] is an L2-normalised vector, or null when the
 * embedding backend was unavailable.
 */
data class EpisodicEntry(
    val id: String,
    val userText: String,
    val assistantText: String,
    val recordedAtUtc: Instant = Instant.now(),
    val appContext: String? = null,
    val embedding: FloatArray? = null,
    val tags: Map<String, String>? = null,
) {
    // FloatArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EpisodicEntry) return false
        return id == other.id &&
            userText == other.userText &&
            assistantText == other.assistantText &&
            recordedAtUtc == other.recordedAtUtc &&
            appContext == other.appContext &&
            (embedding?.contentEquals(other.embedding) ?: (other.embedding == null)) &&
            tags == other.tags
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + userText.hashCode()
        result = 31 * result + assistantText.hashCode()
        result = 31 * result + recordedAtUtc.hashCode()
        result = 31 * result + (appContext?.hashCode() ?: 0)
        result = 31 * result + (embedding?.contentHashCode() ?: 0)
        result = 31 * result + (tags?.hashCode() ?: 0)
        return result
    }
}

// ---------------------------------------------------------------------------
// IEpisodicStore
// ---------------------------------------------------------------------------

/** Persistent store for episodic memories (memory-brain shape). */
interface IEpisodicStore {
    /** Appends a new entry to the store. */
    suspend fun addAsync(entry: EpisodicEntry)

    /**
     * Returns the [topK] entries most similar (cosine) to [queryEmbedding].
     * Falls back to recency when [queryEmbedding] is null or empty.
     */
    suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int = 5): List<EpisodicEntry>

    /** Returns the most recent [count] entries, newest-first. */
    suspend fun getRecentAsync(count: Int = 10): List<EpisodicEntry>

    /** Total number of entries currently stored. */
    suspend fun countAsync(): Int

    /** Removes all entries older than [cutoff]. Returns the number of entries removed. */
    suspend fun pruneOlderThanAsync(cutoff: Instant): Int
}

// ---------------------------------------------------------------------------
// InMemoryEpisodicStore
// ---------------------------------------------------------------------------

/**
 * In-memory [IEpisodicStore]. Capacity is capped (FIFO eviction) to prevent
 * unbounded growth on long-running processes. Thread-safe via a monitor,
 * matching the C# `ReaderWriterLockSlim` and the Go port's mutex.
 *
 * @param maxEntries Cap on stored entries; when exceeded the oldest are evicted
 *   (FIFO). Default 1000. Must be positive.
 */
class InMemoryEpisodicStore(private val maxEntries: Int = 1000) : IEpisodicStore {

    init {
        require(maxEntries > 0) { "maxEntries must be positive" }
    }

    private val lock = Any()
    private val entries = ArrayList<EpisodicEntry>()

    override suspend fun addAsync(entry: EpisodicEntry) {
        synchronized(lock) {
            entries.add(entry)
            // Evict oldest when over capacity.
            while (entries.size > maxEntries) entries.removeAt(0)
        }
    }

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<EpisodicEntry> {
        val snapshot = synchronized(lock) { entries.toList() }

        if (queryEmbedding == null || queryEmbedding.isEmpty()) {
            // No embedding — return most recent.
            return snapshot
                .sortedByDescending { it.recordedAtUtc }
                .take(topK)
        }

        // Cosine similarity, only against entries whose embedding matches the query
        // dimension. Both vectors are L2-normalised, so cosine == dot product.
        return snapshot
            .filter { it.embedding != null && it.embedding.size == queryEmbedding.size }
            .map { it to cosineSimilarity(queryEmbedding, it.embedding!!) }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    override suspend fun getRecentAsync(count: Int): List<EpisodicEntry> {
        val snapshot = synchronized(lock) { entries.toList() }
        return snapshot
            .sortedByDescending { it.recordedAtUtc }
            .take(count)
    }

    override suspend fun countAsync(): Int = synchronized(lock) { entries.size }

    override suspend fun pruneOlderThanAsync(cutoff: Instant): Int = synchronized(lock) {
        val before = entries.size
        entries.removeAll { it.recordedAtUtc.isBefore(cutoff) }
        before - entries.size
    }

    private companion object {
        /** Cosine similarity of two equal-length, L2-normalised vectors (== dot product). */
        fun cosineSimilarity(a: FloatArray, b: FloatArray): Float {
            var dot = 0f
            for (i in a.indices) dot += a[i] * b[i]
            return dot
        }
    }
}
