// PrefixCacheService.kt
//
// Kotlin port of CircleAI.Inference.PrefixCacheService (RT-06 cross-session
// prefix cache). C# is the EXACT spec. Manages an on-disk cache of "warm"
// model sessions keyed by the hash of (modelId, systemPrompt). Generators that
// opt in via GenerationOptions.usePrefixCache consult this service before
// resetting the model handle for a new conversation.
//
// Cache layout:
//   %LOCALAPPDATA%/CircleAI/prefix-cache/    (Windows)
//   ~/.circleai/prefix-cache/                (Unix/iOS/Android)
//     <modelHash>_<systemHash>.session   ← native KV snapshot
//     <modelHash>_<systemHash>.meta      ← JSON metadata
//
// Eviction: LRU by file mtime, cap at 500 MB total, oldest first.

package com.bhengubv.circleai.inference

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.security.MessageDigest
import java.time.Instant

/**
 * Manages an on-disk cache of "warm" model sessions keyed by the hash of
 * (modelId, systemPrompt).
 *
 * The service is thread-safe (I/O serialised through a [Mutex]) and shared
 * across generators; the default instance is [Default]. Override the root
 * directory via the constructor for tests.
 *
 * @param root Directory to root the cache at. Created on demand.
 */
class PrefixCacheService(root: String) {

    private val root: String

    init {
        require(root.isNotBlank()) { "root is required." }
        this.root = root
        File(root).mkdirs()
    }

    /** Returns the cache path for [key]. May or may not exist; use [hasEntryAsync]. */
    fun pathFor(key: String): String = File(root, "$key.session").path

    /** `true` when a cached entry exists for [key]. */
    suspend fun hasEntryAsync(key: String): Boolean = File(pathFor(key)).exists()

    /**
     * Touch the entry's mtime so LRU eviction treats it as recently used.
     * Called after a successful load.
     */
    fun touch(key: String) {
        val f = File(pathFor(key))
        if (f.exists()) f.setLastModified(System.currentTimeMillis())
    }

    /**
     * Evict oldest entries until the directory is under [CAP_BYTES]. Called
     * after every successful save to keep the cache bounded. Best-effort —
     * failures are swallowed.
     */
    suspend fun evictIfNeededAsync() {
        ioLock.withLock {
            val dir = File(root)
            if (!dir.exists()) return
            val files = (dir.listFiles { f -> f.isFile && f.name.endsWith(".session") } ?: emptyArray())
                .sortedBy { it.lastModified() }
            var total = files.sumOf { it.length() }
            var i = 0
            while (total > CAP_BYTES && i < files.size) {
                val f = files[i++]
                try {
                    total -= f.length()
                    f.delete()
                } catch (_: Exception) {
                    // best effort
                }
            }
        }
    }

    companion object {
        private const val CAP_BYTES: Long = 500L * 1024 * 1024 // 500 MB
        private val ioLock = Mutex()

        /**
         * The default per-app instance rooted at `%LOCALAPPDATA%/CircleAI/prefix-cache`
         * on Windows and `~/.circleai/prefix-cache` on Unix.
         */
        val Default: PrefixCacheService by lazy { PrefixCacheService(defaultRoot()) }

        /**
         * Compute the cache key for a (modelId, systemPrompt) pair. Returns
         * `null` when [systemPrompt] is null/empty — there is nothing to cache
         * without a system prompt to key against.
         */
        fun keyFor(modelId: String, systemPrompt: String?): String? {
            if (modelId.isBlank()) return null
            if (systemPrompt.isNullOrEmpty()) return null
            val modelHash = sha256(modelId)
            val systemHash = sha256(systemPrompt)
            // First 16 hex chars per component — collision-free at device scale.
            return "${modelHash.substring(0, 16)}_${systemHash.substring(0, 16)}"
        }

        private fun sha256(input: String): String {
            val bytes = MessageDigest.getInstance("SHA-256").digest(input.toByteArray(Charsets.UTF_8))
            val sb = StringBuilder(bytes.size * 2)
            for (b in bytes) sb.append("%02x".format(b))
            return sb.toString()
        }

        private fun defaultRoot(): String {
            val local = System.getenv("LOCALAPPDATA")
            if (!local.isNullOrBlank()) {
                return File(File(local, "CircleAI"), "prefix-cache").path
            }
            val home = System.getProperty("user.home") ?: "."
            return File(File(home, ".circleai"), "prefix-cache").path
        }
    }
}

/**
 * Metadata sidecar for a cached prefix entry (matches the C# `.meta` JSON:
 * createdAtUtc + modelId). Written alongside the `.session` snapshot.
 */
data class PrefixCacheMeta(
    val modelId: String,
    val createdAtUtc: Instant,
)
