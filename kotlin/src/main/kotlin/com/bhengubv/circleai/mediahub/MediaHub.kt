// MediaHub.kt
//
// Kotlin port of CircleAI.MediaHub (Contracts.cs + InMemoryMediaHub.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Media-server
// contracts + a real in-memory library and a broadcast/subscribe synced-playback
// hub.
//
// Design fidelity notes:
//   * C# `record MediaItem` / `PlaybackPosition`  -> Kotlin `data class`.
//   * C# `TimeSpan`                               -> `java.time.Duration`.
//   * C# `DateTimeOffset`                         -> `java.time.Instant`.
//   * C# `ValueTask<T>` / `ValueTask`             -> `suspend fun`.
//   * C# `Func<PlaybackPosition, ValueTask>`      -> `suspend (PlaybackPosition) -> Unit`.
//   * C# `IDisposable` (returned by Subscribe)    -> `AutoCloseable`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`.
//   * C# `SearchAsync` orders by Title ASCENDING (OrdinalIgnoreCase) — NOT by
//     date — capped at topK; reproduced exactly.
//   * BroadcastPositionAsync SNAPSHOTS the subscriber list under the lock, then
//     RELEASES the lock before awaiting each handler (never invoke a handler while
//     holding the session lock — the handler / its unsubscribe path re-acquires it).
//   * A subscriber that throws is swallowed (logged in C# via Debug.WriteLine),
//     exactly as the reference does, so one bad subscriber cannot break the fan-out.

package com.bhengubv.circleai.mediahub

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One media item exposed by a media backend. Mirrors C# `MediaItem`. */
data class MediaItem(
    val itemId: String,
    val title: String,
    val kind: String,
    val duration: Duration,
    val mimeType: String,
)

/** A playback cursor broadcast to a synced session. Mirrors C# `PlaybackPosition`. */
data class PlaybackPosition(
    val itemId: String,
    val position: Duration,
    val atUtc: Instant,
)

/** Async, backend-identified media catalog. Mirrors C# `IMediaLibrary`. */
interface IMediaLibrary {
    /** Backend self-identification — "in-memory", "null". */
    val backendId: String

    /** Look up an item by id, or null if absent. */
    suspend fun getAsync(id: String): MediaItem?

    /** Title-substring search (case-insensitive), capped at [topK]. */
    suspend fun searchAsync(query: String, topK: Int = 20): List<MediaItem>
}

/** Broadcast/subscribe playback synchronisation. Mirrors C# `ISyncedPlayback`. */
interface ISyncedPlayback {
    /** Backend self-identification — "in-memory", "null". */
    val backendId: String

    /** Register [userId] as a member of [sessionId] (idempotent). */
    suspend fun joinSessionAsync(sessionId: String, userId: String)

    /** Fan [pos] out to every subscriber of [sessionId]. */
    suspend fun broadcastPositionAsync(sessionId: String, pos: PlaybackPosition)

    /**
     * Subscribe [handler] to [sessionId]'s position broadcasts. Dispose the
     * returned token to unsubscribe. Mirrors C#
     * `IDisposable Subscribe(string, Func<PlaybackPosition, ValueTask>)`.
     */
    fun subscribe(sessionId: String, handler: suspend (PlaybackPosition) -> Unit): AutoCloseable
}

// =====================================================================
// InMemoryMediaLibrary (InMemoryMediaHub.cs)
// =====================================================================

/**
 * Title-substring searchable media library backed by a dictionary. Mirrors C#
 * `InMemoryMediaLibrary` — search matches case-insensitively and orders by title
 * ascending (ordinal-ignore-case).
 */
class InMemoryMediaLibrary : IMediaLibrary {
    private val items = ConcurrentHashMap<String, MediaItem>()

    override val backendId: String get() = "in-memory"

    /** Seed the library with an item (insert or replace, keyed by [MediaItem.itemId]). */
    fun add(item: MediaItem) {
        // ArgumentNullException.ThrowIfNull(item) — Kotlin non-null type enforces this.
        items[item.itemId] = item
    }

    override suspend fun getAsync(id: String): MediaItem? {
        require(id.isNotBlank()) { "id required" }
        return items[id]
    }

    override suspend fun searchAsync(query: String, topK: Int): List<MediaItem> {
        // C#: null query throws; Kotlin non-null String enforces this.
        require(topK > 0) { "topK" }
        return items.values
            .filter { it.title.contains(query, ignoreCase = true) }
            .sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.title })
            .take(topK)
    }
}

// =====================================================================
// InMemorySyncedPlayback (InMemoryMediaHub.cs)
// =====================================================================

/**
 * In-memory broadcast/subscribe playback sync. Per-session membership + a
 * subscriber list; [broadcastPositionAsync] snapshots the subscribers under the
 * session lock, releases it, then invokes each handler. Mirrors C#
 * `InMemorySyncedPlayback`.
 */
class InMemorySyncedPlayback : ISyncedPlayback {

    private class SessionState {
        val members = HashSet<String>()
        val subscribers = ArrayList<suspend (PlaybackPosition) -> Unit>()
    }

    private val sessions = ConcurrentHashMap<String, SessionState>()

    override val backendId: String get() = "in-memory"

    override suspend fun joinSessionAsync(sessionId: String, userId: String) {
        require(sessionId.isNotBlank()) { "sessionId required" }
        require(userId.isNotBlank()) { "userId required" }

        val state = sessions.computeIfAbsent(sessionId) { SessionState() }
        synchronized(state) {
            state.members.add(userId)
        }
    }

    override suspend fun broadcastPositionAsync(sessionId: String, pos: PlaybackPosition) {
        // ArgumentNullException.ThrowIfNull(pos) — Kotlin non-null type enforces this.
        require(sessionId.isNotBlank()) { "sessionId required" }
        val state = sessions[sessionId] ?: return

        // Snapshot subscribers UNDER the lock, then RELEASE before awaiting each
        // handler — a handler (or its unsubscribe path) re-acquires this lock, so
        // invoking it while held would deadlock a non-reentrant guard.
        val snapshot: List<suspend (PlaybackPosition) -> Unit> =
            synchronized(state) { state.subscribers.toList() }

        for (sub in snapshot) {
            try {
                sub(pos)
            } catch (ex: Throwable) {
                // Mirror C# Debug.WriteLine: one bad subscriber must not break fan-out.
                System.err.println("[CircleAI.MediaHub] playback subscriber threw: ${ex.message}")
            }
        }
    }

    override fun subscribe(sessionId: String, handler: suspend (PlaybackPosition) -> Unit): AutoCloseable {
        require(sessionId.isNotBlank()) { "sessionId required" }
        // ArgumentNullException.ThrowIfNull(handler) — Kotlin non-null type enforces this.
        val state = sessions.computeIfAbsent(sessionId) { SessionState() }
        synchronized(state) { state.subscribers.add(handler) }
        return SubscriptionToken(sessionId, handler)
    }

    private inner class SubscriptionToken(
        private val sessionId: String,
        private val handler: suspend (PlaybackPosition) -> Unit,
    ) : AutoCloseable {
        override fun close() {
            val state = sessions[sessionId] ?: return
            synchronized(state) { state.subscribers.remove(handler) }
        }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op media library — always empty. Mirrors C# `NullMediaLibrary`. */
class NullMediaLibrary private constructor() : IMediaLibrary {
    companion object {
        val Instance = NullMediaLibrary()
    }

    override val backendId: String get() = "null"
    override suspend fun getAsync(id: String): MediaItem? = null
    override suspend fun searchAsync(query: String, topK: Int): List<MediaItem> = emptyList()
}

/** No-op synced playback — accepts everything, delivers nothing. Mirrors C# `NullSyncedPlayback`. */
class NullSyncedPlayback private constructor() : ISyncedPlayback {
    companion object {
        val Instance = NullSyncedPlayback()
    }

    override val backendId: String get() = "null"
    override suspend fun joinSessionAsync(sessionId: String, userId: String) {}
    override suspend fun broadcastPositionAsync(sessionId: String, pos: PlaybackPosition) {}
    override fun subscribe(sessionId: String, handler: suspend (PlaybackPosition) -> Unit): AutoCloseable =
        EmptyDisposable

    private object EmptyDisposable : AutoCloseable {
        override fun close() {}
    }
}
