// ModelDownloadServiceTest.kt
//
// Verifies CircleAI.Inference.ModelDownloadService: single-file ensure with
// SHA-256 verify + cache hit, bundle ensure with primary→fallback failover,
// mismatch deletion, installed.json round-trip, cache/delete, and the
// StripShaAlgorithmPrefix / URL-builder helpers. External I/O is injected via
// an in-memory IByteFetcher (no real sockets).

package com.bhengubv.circleai.inference

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class ModelDownloadServiceTest {

    /** In-memory fetcher: serves bytes from a URL→bytes map; unknown URLs throw. */
    private class MapFetcher(private val content: Map<String, ByteArray>) : IByteFetcher {
        val fetched = ArrayList<String>()
        override suspend fun fetchToFileAsync(uri: String, dest: File, progress: ((Double) -> Unit)?) {
            fetched.add(uri)
            val bytes = content[uri] ?: throw java.io.IOException("404: $uri")
            dest.parentFile?.mkdirs()
            dest.writeBytes(bytes)
            progress?.invoke(1.0)
        }
    }

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    private fun tempStorage(): String = Files.createTempDirectory("models").toFile().absolutePath

    // ── helpers ──────────────────────────────────────────────────────────────

    @Test
    fun `strips sha256 algorithm prefix`() {
        assertEquals("abc123", ModelDownloadService.stripShaAlgorithmPrefix("sha256:abc123"))
        assertEquals("abc123", ModelDownloadService.stripShaAlgorithmPrefix("SHA-256: abc123 "))
        assertEquals("deadbeef", ModelDownloadService.stripShaAlgorithmPrefix("deadbeef"))
    }

    @Test
    fun `builds primary and fallback urls`() {
        assertEquals(
            "https://modelscope.cn/api/v1/models/MNN/Qwen3-0.6B-MNN/repo?Revision=master&FilePath=config.json",
            ModelDownloadService.buildPrimaryUrl("MNN/Qwen3-0.6B-MNN", "config.json"),
        )
        assertEquals(
            "https://modelscope.cn/models/MNN/Qwen3-0.6B-MNN/resolve/master/config.json",
            ModelDownloadService.buildFallbackUrl("MNN/Qwen3-0.6B-MNN", "config.json"),
        )
    }

    // ── single-file ──────────────────────────────────────────────────────────

    @Test
    fun `ensure single file downloads, verifies, and caches`() = runTest {
        val bytes = "weights".toByteArray()
        val url = "https://example/model.gguf"
        val fetcher = MapFetcher(mapOf(url to bytes))
        val svc = ModelDownloadService(tempStorage(), fetcher)

        val path = svc.ensureModelAsync("m", url, "sha256:${sha256Hex(bytes)}", null)
        assertTrue(File(path).exists())
        assertEquals(1, fetcher.fetched.size)

        // Second call: cache hit — no new fetch.
        val path2 = svc.ensureModelAsync("m", url, "sha256:${sha256Hex(bytes)}", null)
        assertEquals(path, path2)
        assertEquals(1, fetcher.fetched.size)
        assertTrue(svc.isModelCachedAsync("m"))
    }

    @Test
    fun `sha mismatch deletes the temp file and throws`() = runTest {
        val bytes = "weights".toByteArray()
        val url = "https://example/model.gguf"
        val svc = ModelDownloadService(tempStorage(), MapFetcher(mapOf(url to bytes)))
        assertFailsWith<IllegalStateException> {
            svc.ensureModelAsync("m", url, "sha256:${"0".repeat(64)}", null)
        }
        assertFalse(svc.isModelCachedAsync("m"))
    }

    // ── bundle ─────────────────────────────────────────────────────────────

    @Test
    fun `ensure bundle fails over from primary to fallback and writes manifest`() = runTest {
        val cfg = "{}".toByteArray()
        val weights = byteArrayOf(1, 2, 3, 4)
        val repo = "MNN/Test"
        // Primary URLs are NOT in the map → fetcher throws → fallback used.
        val fallbackCfg = ModelDownloadService.buildFallbackUrl(repo, "config.json")
        val fallbackW = ModelDownloadService.buildFallbackUrl(repo, "weights.bin")
        val fetcher = MapFetcher(mapOf(fallbackCfg to cfg, fallbackW to weights))
        val storage = tempStorage()
        val svc = ModelDownloadService(storage, fetcher)

        val files = listOf(
            BundleFileSpec("config.json", "sha256:${sha256Hex(cfg)}", cfg.size.toLong()),
            BundleFileSpec("weights.bin", sha256Hex(weights), weights.size.toLong()),
        )
        val dir = svc.ensureBundleAsync("qwen", repo, files, null)
        assertTrue(File(dir, "config.json").exists())
        assertTrue(File(dir, "weights.bin").exists())
        // Both primary attempts happened then both fallbacks.
        assertTrue(fetcher.fetched.any { it.contains("resolve/master") })

        // installed.json round-trips.
        svc.writeInstalledManifestAsync(dir, "qwen", "1.2.0", repo, files)
        val manifest = svc.readInstalledManifest(dir)
        assertNotNull(manifest)
        assertEquals("qwen", manifest.modelId)
        assertEquals("1.2.0", manifest.version)
        assertEquals(2, manifest.files.size)
    }

    @Test
    fun `bundle skips files already present and valid`() = runTest {
        val cfg = "{}".toByteArray()
        val repo = "MNN/Test"
        val primaryCfg = ModelDownloadService.buildPrimaryUrl(repo, "config.json")
        val fetcher = MapFetcher(mapOf(primaryCfg to cfg))
        val storage = tempStorage()
        val svc = ModelDownloadService(storage, fetcher)
        val files = listOf(BundleFileSpec("config.json", sha256Hex(cfg), cfg.size.toLong()))

        svc.ensureBundleAsync("q", repo, files, null)
        val firstCount = fetcher.fetched.size
        // Re-run: cached + valid → no re-fetch.
        svc.ensureBundleAsync("q", repo, files, null)
        assertEquals(firstCount, fetcher.fetched.size)
    }

    @Test
    fun `delete removes single file and directory`() = runTest {
        val bytes = "x".toByteArray()
        val url = "https://example/m.gguf"
        val svc = ModelDownloadService(tempStorage(), MapFetcher(mapOf(url to bytes)))
        svc.ensureModelAsync("m", url, null, null)
        assertTrue(svc.isModelCachedAsync("m"))
        svc.deleteModelAsync("m")
        assertFalse(svc.isModelCachedAsync("m"))
    }

    @Test
    fun `available disk space is non-negative`() = runTest {
        val svc = ModelDownloadService(tempStorage(), MapFetcher(emptyMap()))
        assertTrue(svc.getAvailableDiskSpaceBytesAsync() >= 0)
    }

    @Test
    fun `blank model id is rejected`() = runTest {
        val svc = ModelDownloadService(tempStorage(), MapFetcher(emptyMap()))
        assertFailsWith<IllegalArgumentException> { svc.ensureModelAsync("  ", "u", null, null) }
    }
}
