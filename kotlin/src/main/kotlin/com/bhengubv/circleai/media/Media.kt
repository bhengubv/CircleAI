// Media.kt
//
// Kotlin port of CircleAI.Media (MediaPrimitives.cs) — the C# reference is the
// EXACT spec. Real domain types + in-memory library for the Media vertical
// (audio + video + image asset catalog).
//
// Design fidelity notes:
//   * C# `enum MediaKind`                        -> Kotlin `enum class MediaKind`.
//   * C# `sealed record MediaAsset`              -> Kotlin `data class MediaAsset`.
//   * C# `TimeSpan?`                             -> `java.time.Duration?` (nullable).
//   * C# `DateTimeOffset`                        -> `java.time.Instant`.
//   * C# `long Bytes`                            -> `Long`.
//   * C# `ConcurrentDictionary<string, MediaAsset>` (Ordinal comparer)
//                                                -> `java.util.concurrent.ConcurrentHashMap`
//     (keys compared by String structural equality == ordinal).
//   * C# `Title.Contains(q, OrdinalIgnoreCase)`  -> `title.contains(q, ignoreCase = true)`.
//   * C# `OrderByDescending(a => a.CreatedAtUtc)` is a STABLE sort; Kotlin
//     `sortedByDescending` is likewise stable, so tie order is preserved.
//   * Argument validation mirrors the C# throws exactly (null/blank AssetId,
//     null query, non-positive topK).

package com.bhengubv.circleai.media

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** Kind of media asset. Mirrors C# `MediaKind`. */
enum class MediaKind { Audio, Video, Image }

/**
 * One catalogued media asset. Mirrors C# `MediaAsset` record.
 *
 * @param assetId Stable identifier (library key).
 * @param title Human-visible title (searched by substring).
 * @param kind Audio / Video / Image.
 * @param duration Playback duration, or null for images / unknown.
 * @param bytes Size on disk in bytes.
 * @param mime MIME type (e.g. `audio/mpeg`).
 * @param createdAtUtc Ingestion timestamp (UTC).
 */
data class MediaAsset(
    val assetId: String,
    val title: String,
    val kind: MediaKind,
    val duration: Duration?,
    val bytes: Long,
    val mime: String,
    val createdAtUtc: Instant,
)

/** Read/write catalog of [MediaAsset]s. Mirrors C# `IMediaLibrary`. */
interface IMediaLibrary {
    /** Insert or replace an asset (keyed by [MediaAsset.assetId]). */
    fun add(a: MediaAsset)

    /** Look up an asset by id, or null if absent. */
    fun get(id: String): MediaAsset?

    /** Remove an asset by id. Returns true if it was present. */
    fun remove(id: String): Boolean

    /** Number of assets currently catalogued. */
    val count: Int

    /** Total on-disk footprint of every catalogued asset, in bytes. */
    val totalBytes: Long

    /** Every asset of [kind], newest first. */
    fun listByKind(kind: MediaKind): List<MediaAsset>

    /** Assets whose MIME type starts with [mimePrefix] (case-insensitive), newest first. */
    fun byMime(mimePrefix: String): List<MediaAsset>

    /** Title-substring search (case-insensitive), newest first, capped at [topK]. */
    fun search(q: String, topK: Int = 20): List<MediaAsset>
}

/**
 * Dictionary-backed [IMediaLibrary]. Title-substring (case-insensitive) search;
 * results ordered by [MediaAsset.createdAtUtc] descending. Mirrors C#
 * `InMemoryMediaLibrary`.
 */
class InMemoryMediaLibrary : IMediaLibrary {
    private val items = ConcurrentHashMap<String, MediaAsset>()

    override fun add(a: MediaAsset) {
        // ArgumentNullException.ThrowIfNull(a) — Kotlin non-null type enforces this.
        require(a.assetId.isNotBlank()) { "AssetId required" }
        items[a.assetId] = a
    }

    override fun get(id: String): MediaAsset? = items[id]

    override fun remove(id: String): Boolean =
        id.isNotEmpty() && items.remove(id) != null

    override val count: Int
        get() = items.size

    override val totalBytes: Long
        get() = items.values.sumOf { it.bytes }

    override fun listByKind(kind: MediaKind): List<MediaAsset> =
        items.values
            .filter { it.kind == kind }
            .sortedByDescending { it.createdAtUtc }

    override fun byMime(mimePrefix: String): List<MediaAsset> {
        if (mimePrefix.isEmpty()) return emptyList()
        return items.values
            .filter { it.mime.startsWith(mimePrefix, ignoreCase = true) }
            .sortedByDescending { it.createdAtUtc }
    }

    override fun search(q: String, topK: Int): List<MediaAsset> {
        // C#: `if (q is null) throw new ArgumentNullException` — Kotlin non-null
        // String enforces non-null; keep the topK bound check identical.
        require(topK > 0) { "topK" }
        return items.values
            .filter { it.title.contains(q, ignoreCase = true) }
            .sortedByDescending { it.createdAtUtc }
            .take(topK)
    }
}
