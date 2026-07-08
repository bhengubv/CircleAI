// Multimodal.kt
//
// Compressed semantic memory for media artefacts (image / audio / video /
// document). Kotlin port of CircleAI.Memory.Multimodal (the C# reference),
// mirroring the verified TypeScript pilot (memory/multimodal.ts) 1:1:
//   • MediaModality, MultimodalMemoryEntry (+ makeMultimodalMemoryEntry)
//   • IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
//   • IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
//   • MultimodalMemoryIngester (+ IngestionResult, IngestOptions)
//
// The whole point: we DO NOT store the pixels / audio samples / video frames —
// we store the caption, the embedding, and a SHA-256 of the original so the
// host can reference it back if it kept the file elsewhere. Raw bytes never
// leave the captioner; the store only ever holds the semantic record.

package com.bhengubv.circleai.memory.brain

import java.security.MessageDigest
import java.time.Instant
import java.util.UUID

// ---------------------------------------------------------------------------
// MediaModality
// ---------------------------------------------------------------------------

/**
 * Modality of a multimodal memory entry. Drives how the ingester routes the raw
 * bytes to the captioner and which side-channel metadata is captured.
 */
enum class MediaModality {
    /** Still image — JPEG, PNG, HEIC, WebP, AVIF. */
    Image,

    /** Audio clip — Opus, WAV, MP3, M4A. */
    Audio,

    /** Video — MP4, MOV, WebM. Captioned via key-frame extraction by the host. */
    Video,

    /** Text document — PDF, DOCX, plain text snippet larger than a single message. */
    TextDocument,
}

// ---------------------------------------------------------------------------
// MultimodalMemoryEntry
// ---------------------------------------------------------------------------

/**
 * One semantically-compressed media memory. The caption + embedding capture the
 * meaning; raw bytes are never retained by the memory layer.
 *
 * [referenceCount] is mutable (incremented on dedup hits); everything else is
 * effectively write-once, matching the C# `init`/`set` split.
 */
class MultimodalMemoryEntry(
    /** Stable identifier (UUID v4). */
    val id: String = UUID.randomUUID().toString(),
    /** UTC timestamp the memory was recorded. */
    val recordedAtUtc: Instant = Instant.now(),
    /** Which kind of media this came from. */
    val modality: MediaModality = MediaModality.Image,
    /** Caption — the semantic content. */
    val caption: String = "",
    /** Embedding of the caption (and, for richer captioners, the joint embedding). */
    val embedding: FloatArray? = null,
    /**
     * SHA-256 of the original bytes, hex-lower. Lets the host dedupe, reference
     * a kept file, and verify a re-uploaded file matches what was remembered.
     */
    val sourceSha256: String = "",
    /** Original MIME type (e.g. image/jpeg). Captured for diagnostics. */
    val sourceMimeType: String? = null,
    /** Size in bytes of the original artefact. */
    val sourceByteCount: Long = 0,
    /** Optional URI of the original artefact if the host retained it elsewhere. */
    val sourceUri: String? = null,
    /** Image / video width in pixels, when applicable. */
    val widthPx: Int? = null,
    /** Image / video height in pixels, when applicable. */
    val heightPx: Int? = null,
    /** Audio / video duration in milliseconds, when applicable. */
    val durationMs: Long? = null,
    /**
     * How many times this artefact has been re-presented to the ingester.
     * Incremented on every dedup hit instead of creating a new entry. Mutable.
     */
    var referenceCount: Int = 1,
    /** Optional tags (e.g. location, person, topic). */
    val tags: Map<String, String>? = null,
)

// ---------------------------------------------------------------------------
// IMultimodalCaptioner + CaptionResult
// ---------------------------------------------------------------------------

/** Output of a single captioning call. */
data class CaptionResult(
    /** Human-readable semantic description of the artefact. Must not be empty. */
    val caption: String,
    /** Embedding of the artefact. Null when the captioner has no embedding backend. */
    val embedding: FloatArray? = null,
    /** Image / video width when known. */
    val widthPx: Int? = null,
    /** Image / video height when known. */
    val heightPx: Int? = null,
    /** Audio / video duration when known. */
    val durationMs: Long? = null,
)

/** Converts raw media bytes into a semantic representation. */
interface IMultimodalCaptioner {
    /**
     * True when this captioner can handle the given modality + mime. The
     * ingester picks among multiple captioners using this predicate.
     */
    fun canCaption(modality: MediaModality, mimeType: String?): Boolean

    /**
     * Produces a [CaptionResult] for the given source bytes. Implementations
     * must not retain the bytes after the call returns.
     */
    suspend fun captionAsync(
        modality: MediaModality,
        sourceBytes: ByteArray,
        mimeType: String?,
    ): CaptionResult
}

/**
 * Default [IMultimodalCaptioner]. Returns a descriptive shell caption — never
 * fabricates semantic content. Always available, zero model dependency, zero
 * token cost.
 */
class HeuristicMultimodalCaptioner : IMultimodalCaptioner {
    override fun canCaption(modality: MediaModality, mimeType: String?): Boolean = true

    override suspend fun captionAsync(
        modality: MediaModality,
        sourceBytes: ByteArray,
        mimeType: String?,
    ): CaptionResult {
        val detected = detectMime(sourceBytes, mimeType)
        val len = sourceBytes.size
        val caption = when (modality) {
            MediaModality.Image -> "[Image — no captioner wired. $detected, $len bytes.]"
            MediaModality.Audio -> "[Audio — no captioner wired. $detected, $len bytes.]"
            MediaModality.Video -> "[Video — no captioner wired. $detected, $len bytes.]"
            MediaModality.TextDocument -> "[Document — no captioner wired. $detected, $len bytes.]"
        }
        return CaptionResult(caption = caption, embedding = null)
    }

    private companion object {
        fun detectMime(bytes: ByteArray, declared: String?): String {
            if (declared != null && declared.isNotBlank()) return declared
            if (bytes.size >= 4) {
                val b0 = bytes[0].toInt() and 0xFF
                val b1 = bytes[1].toInt() and 0xFF
                val b2 = bytes[2].toInt() and 0xFF
                val b3 = bytes[3].toInt() and 0xFF
                if (b0 == 0xFF && b1 == 0xD8) return "image/jpeg"
                if (b0 == 0x89 && b1 == 0x50 && b2 == 0x4E && b3 == 0x47) return "image/png"
                if (b0 == 0x47 && b1 == 0x49 && b2 == 0x46) return "image/gif"
                if (b0 == 0x52 && b1 == 0x49 && b2 == 0x46 && b3 == 0x46) return "audio/wav"
                if (b0 == 0x25 && b1 == 0x50 && b2 == 0x44 && b3 == 0x46) return "application/pdf"
            }
            return "application/octet-stream"
        }
    }
}

// ---------------------------------------------------------------------------
// IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
// ---------------------------------------------------------------------------

/** Persistent store of compressed multimodal memories. */
interface IMultimodalMemoryStore {
    /** Adds an entry. Duplicate SHA-256 hits should be handled via [getByHashAsync]. */
    suspend fun addAsync(entry: MultimodalMemoryEntry)

    /** Returns the entry with the given hash, or null if unknown. */
    suspend fun getByHashAsync(sourceSha256: String): MultimodalMemoryEntry?

    /** Increments referenceCount for the entry whose hash matches. No-op when unknown. */
    suspend fun reinforceAsync(sourceSha256: String)

    /**
     * Returns the top-[topK] entries whose embedding is most similar (cosine) to
     * [queryEmbedding]. When the query is null, falls back to most-recent.
     */
    suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int = 5): List<MultimodalMemoryEntry>

    /** Returns the most recent [count] entries. */
    suspend fun getRecentAsync(count: Int = 10): List<MultimodalMemoryEntry>

    /** Removes entries older than [cutoff]. Returns count removed. */
    suspend fun pruneOlderThanAsync(cutoff: Instant): Int

    /** Total entries currently stored. */
    suspend fun countAsync(): Int
}

/** In-memory [IMultimodalMemoryStore]. Keyed by SHA-256 (case-insensitive). */
class InMemoryMultimodalMemoryStore : IMultimodalMemoryStore {

    // C# uses a ConcurrentDictionary with OrdinalIgnoreCase; we lower-case the
    // key to reproduce case-insensitive hash lookups. A LinkedHashMap preserves
    // insertion order, matching the C# dictionary enumeration used by search /
    // recent tie-breaking.
    private val lock = Any()
    private val byHash = LinkedHashMap<String, MultimodalMemoryEntry>()

    override suspend fun addAsync(entry: MultimodalMemoryEntry) {
        require(entry.sourceSha256.isNotBlank()) { "SourceSha256 is required." }
        synchronized(lock) { byHash[keyOf(entry.sourceSha256)] = entry }
    }

    override suspend fun getByHashAsync(sourceSha256: String): MultimodalMemoryEntry? =
        synchronized(lock) { byHash[keyOf(sourceSha256)] }

    override suspend fun reinforceAsync(sourceSha256: String) {
        synchronized(lock) { byHash[keyOf(sourceSha256)]?.let { it.referenceCount++ } }
    }

    override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<MultimodalMemoryEntry> {
        val snapshot = synchronized(lock) { byHash.values.toList() }

        if (queryEmbedding == null) {
            return snapshot
                .sortedByDescending { it.recordedAtUtc }
                .take(topK)
        }

        return snapshot
            .filter { it.embedding != null && it.embedding.isNotEmpty() }
            .map { it to cosineScore(queryEmbedding, it.embedding!!) }
            .sortedByDescending { it.second }
            .take(topK)
            .map { it.first }
    }

    override suspend fun getRecentAsync(count: Int): List<MultimodalMemoryEntry> {
        val snapshot = synchronized(lock) { byHash.values.toList() }
        return snapshot
            .sortedByDescending { it.recordedAtUtc }
            .take(count)
    }

    override suspend fun pruneOlderThanAsync(cutoff: Instant): Int = synchronized(lock) {
        val doomed = byHash.values
            .filter { it.recordedAtUtc.isBefore(cutoff) }
            .map { keyOf(it.sourceSha256) }
        for (h in doomed) byHash.remove(h)
        doomed.size
    }

    override suspend fun countAsync(): Int = synchronized(lock) { byHash.size }

    private companion object {
        fun keyOf(sha: String): String = sha.lowercase()
    }
}

// ---------------------------------------------------------------------------
// MultimodalMemoryIngester
// ---------------------------------------------------------------------------

/** Outcome of a [MultimodalMemoryIngester.ingestAsync] call. */
data class IngestionResult(
    val entry: MultimodalMemoryEntry,
    val wasDeduplicated: Boolean,
)

/** Optional per-call inputs for [MultimodalMemoryIngester.ingestAsync]. */
data class IngestOptions(
    /** Optional MIME type for the source. */
    val mimeType: String? = null,
    /** Optional URI of the original (host-retained). */
    val sourceUri: String? = null,
    /** Optional caller-supplied tags. */
    val tags: Map<String, String>? = null,
)

/**
 * Ingests raw media bytes into compressed semantic memory.
 *
 *   1. Hashes the source (SHA-256, hex-lower).
 *   2. Dedupes — if the hash is known, reinforces the existing entry and
 *      returns it (no re-captioning, no duplicate storage).
 *   3. Picks a captioner via [IMultimodalCaptioner.canCaption].
 *   4. Asks the captioner for a [CaptionResult].
 *   5. Persists a [MultimodalMemoryEntry] to the store.
 *
 * Raw bytes are never persisted. The hash is the only durable handle the memory
 * layer keeps for the original artefact.
 *
 * Captioners are tried in order — the first one whose [IMultimodalCaptioner.canCaption]
 * returns true wins. The host typically registers richer captioners first and
 * the heuristic fallback last.
 */
class MultimodalMemoryIngester(
    captioners: Iterable<IMultimodalCaptioner>,
    private val store: IMultimodalMemoryStore,
) {
    private val captioners: List<IMultimodalCaptioner> = captioners.toList()

    init {
        require(this.captioners.isNotEmpty()) { "At least one captioner is required." }
    }

    /**
     * Ingests an artefact. When the SHA-256 matches an existing entry the stored
     * record is reinforced rather than re-captioned, and the result's
     * [IngestionResult.wasDeduplicated] is true.
     */
    suspend fun ingestAsync(
        modality: MediaModality,
        sourceBytes: ByteArray,
        options: IngestOptions = IngestOptions(),
    ): IngestionResult {
        require(sourceBytes.isNotEmpty()) { "Source bytes are empty." }

        val mimeType = options.mimeType
        val hash = computeSha256(sourceBytes)
        val existing = store.getByHashAsync(hash)
        if (existing != null) {
            store.reinforceAsync(hash)
            return IngestionResult(existing, wasDeduplicated = true)
        }

        val captioner = pickCaptioner(modality, mimeType)
        val caption = captioner.captionAsync(modality, sourceBytes, mimeType)

        val entry = MultimodalMemoryEntry(
            modality = modality,
            caption = caption.caption,
            embedding = caption.embedding,
            sourceSha256 = hash,
            sourceMimeType = mimeType,
            sourceByteCount = sourceBytes.size.toLong(),
            sourceUri = options.sourceUri,
            widthPx = caption.widthPx,
            heightPx = caption.heightPx,
            durationMs = caption.durationMs,
            tags = options.tags,
        )

        store.addAsync(entry)
        return IngestionResult(entry, wasDeduplicated = false)
    }

    private fun pickCaptioner(modality: MediaModality, mime: String?): IMultimodalCaptioner {
        for (c in captioners) {
            if (c.canCaption(modality, mime)) return c
        }
        // The last registered captioner should accept everything; if no
        // host-supplied captioner matches, the heuristic fallback wins.
        return captioners.last()
    }

    private companion object {
        fun computeSha256(bytes: ByteArray): String {
            val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
            val sb = StringBuilder(digest.size * 2)
            for (b in digest) {
                val v = b.toInt() and 0xFF
                sb.append(HEX[v ushr 4])
                sb.append(HEX[v and 0x0F])
            }
            return sb.toString()
        }

        private val HEX = "0123456789abcdef".toCharArray()
    }
}

// ---------------------------------------------------------------------------
// Shared cosine — matches the C# stores' internal CosineSimilarity.Score
// ---------------------------------------------------------------------------

/** Cosine similarity — matches the C# store's internal CosineSimilarity.Score. */
internal fun cosineScore(a: FloatArray, b: FloatArray): Float {
    if (a.size != b.size) return 0f
    var dot = 0.0
    var magA = 0.0
    var magB = 0.0
    for (i in a.indices) {
        dot += a[i].toDouble() * b[i]
        magA += a[i].toDouble() * a[i]
        magB += b[i].toDouble() * b[i]
    }
    val denom = Math.sqrt(magA) * Math.sqrt(magB)
    return if (denom < Double.MIN_VALUE) 0f else (dot / denom).toFloat()
}
