// CoreRuntimeTest.kt
//
// Exercises the CircleAI.Core model-management runtime port: model sources +
// downloader fallback chain, LocalModelLoader single-file/bundle behaviour,
// LocalModelManager download+verify, CircleEngine module registry,
// SafeModelHandle / PlatformInterop, and the auditing + multi-tenant surface.

package com.bhengubv.circleai.core

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.File
import java.nio.file.Path
import java.security.MessageDigest
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CoreRuntimeTest {

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    // ── DownloadProgress ─────────────────────────────────────────────────────

    @Test
    fun `DownloadProgress carries the C# fields with sane defaults`() {
        val p = DownloadProgress(fileName = "x.bin", bytesReceived = 5, totalBytes = 10)
        assertEquals("x.bin", p.fileName)
        assertEquals(5, p.bytesReceived)
        assertEquals(10, p.totalBytes)
        assertEquals(0.0, p.bytesPerSecond)
    }

    // ── ModelScopeSource ─────────────────────────────────────────────────────

    @Test
    fun `ModelScopeSource downloads from a modelscope url and reports progress`() = runTest(timeout = kotlin.time.Duration.parse("30s")) {
        val url = "https://modelscope.cn/models/acme/embed/resolve/main/model.bin"
        val body = ByteArray(20000) { (it % 251).toByte() }
        val http = InMemoryModelHttpClient().put(url, body).putText(ModelScopeSource_PROBE, "ok")
        val source = ModelScopeSource(http)
        val out = File(tmpFile("ms.bin"))

        var lastProgress: DownloadProgress? = null
        source.downloadAsync(url, out.path) { lastProgress = it }

        assertContentEquals(body, out.readBytes())
        assertTrue(lastProgress != null)
        assertEquals(body.size.toLong(), lastProgress!!.totalBytes)
        assertEquals(body.size.toLong(), lastProgress!!.bytesReceived)
    }

    @Test
    fun `ModelScopeSource rejects a non-modelscope host`() = runTest {
        val source = ModelScopeSource(InMemoryModelHttpClient())
        assertFailsWith<IllegalArgumentException> {
            source.downloadAsync("https://huggingface.co/x/model.bin", tmpFile("nope.bin"))
        }
    }

    @Test
    fun `ModelScopeSource isAvailable reflects reachability`() = runTest {
        val reachable = ModelScopeSource(InMemoryModelHttpClient().putText(ModelScopeSource_PROBE, "ok"))
        assertTrue(reachable.isAvailableAsync())
        val unreachable = ModelScopeSource(InMemoryModelHttpClient())
        assertFalse(unreachable.isAvailableAsync())
    }

    // ── HuggingFaceSource tombstone ──────────────────────────────────────────

    @Suppress("DEPRECATION_ERROR")
    @Test
    fun `HuggingFaceSource construction throws (removed tombstone)`() {
        assertFailsWith<UnsupportedOperationException> { HuggingFaceSource() }
    }

    // ── ModelDownloader fallback chain ───────────────────────────────────────

    @Test
    fun `ModelDownloader falls through from a failing primary to a working fallback`() = runTest {
        val primary = "https://modelscope.cn/a/model.bin" // not seeded → fails
        val fallback = "https://modelscope.cn/b/model.bin"
        val body = "the-model".toByteArray()
        val http = InMemoryModelHttpClient().put(fallback, body)
        val downloader = ModelDownloader(listOf(ModelScopeSource(http)))

        val out = tmpFile("dl.bin")
        val winner = downloader.downloadFromCandidatesAsync(listOf(primary, fallback), out)
        assertEquals("ModelScope", winner)
        assertContentEquals(body, File(out).readBytes())
    }

    @Test
    fun `ModelDownloader throws when every candidate fails`() = runTest {
        val downloader = ModelDownloader(listOf(ModelScopeSource(InMemoryModelHttpClient())))
        assertFailsWith<IllegalStateException> {
            downloader.downloadFromCandidatesAsync(
                listOf("https://modelscope.cn/x/model.bin"),
                tmpFile("fail.bin"),
            )
        }
    }

    @Test
    fun `ModelDownloader skips a url with no matching source`() = runTest {
        val downloader = ModelDownloader(listOf(ModelScopeSource(InMemoryModelHttpClient())))
        // Non-parseable / non-modelscope host → no source matched → all fail.
        val ex = assertFailsWith<IllegalStateException> {
            downloader.downloadFromCandidatesAsync(listOf("ftp://example.com/x"), tmpFile("skip.bin"))
        }
        assertTrue(ex.message!!.contains("no registered source"))
    }

    @Test
    fun `ModelDownloader downloadModel resolves a registry entry and writes the file`() = runTest {
        val url = "https://modelscope.cn/acme/qwen/resolve/main/model.gguf"
        val body = ByteArray(4096) { it.toByte() }
        val registry = """
            {
              "Notes": "free-text metadata should be skipped",
              "qwen-embed": {
                "FileName": "model.gguf",
                "PrimaryUrl": "$url",
                "SizeBytes": 4096
              }
            }
        """.trimIndent()
        val http = InMemoryModelHttpClient().put(url, body)
        val downloader = ModelDownloader(listOf(ModelScopeSource(http)), registry)

        var report: ModelDownloader.DownloadProgressReport? = null
        downloader.progressChanged = { report = it }

        val dir = tmpDir("dlmodel")
        downloader.downloadModelAsync("qwen-embed", dir)
        assertContentEquals(body, File(dir, "model.gguf").readBytes())
        assertTrue(report != null)
    }

    @Test
    fun `ModelDownloader downloadModel rejects an unknown model id`() = runTest {
        val downloader = ModelDownloader(listOf(ModelScopeSource(InMemoryModelHttpClient())), "{}")
        assertFailsWith<NoSuchElementException> {
            downloader.downloadModelAsync("ghost", tmpDir("ghost"))
        }
    }

    @Test
    fun `ModelDownloader downloadModel steers bundle entries to the multi-file path`() = runTest {
        val registry = """
            {
              "bundle-model": {
                "Repo": "acme/bundle",
                "TotalBytes": 100,
                "BundleFiles": [ { "Name": "llm.mnn.weight", "Sha256": "ab", "SizeBytes": 100 } ]
              }
            }
        """.trimIndent()
        val downloader = ModelDownloader(listOf(ModelScopeSource(InMemoryModelHttpClient())), registry)
        val ex = assertFailsWith<IllegalStateException> {
            downloader.downloadModelAsync("bundle-model", tmpDir("bundle"))
        }
        assertTrue(ex.message!!.contains("multi-file"))
    }

    @Test
    fun `ModelDownloader requires at least one source`() {
        assertFailsWith<IllegalArgumentException> { ModelDownloader(emptyList()) }
    }

    // ── LocalModelLoader ─────────────────────────────────────────────────────

    @Test
    fun `LocalModelLoader downloads a single-file model and verifies its checksum`(@TempDir dir: Path) = runTest {
        val url = "https://modelscope.cn/acme/e/resolve/main/embed.bin"
        val body = "hello-embeddings".toByteArray()
        val checksum = "sha256:" + sha256Hex(body)
        val registry = """
            { "embed": { "FileName": "embed.bin", "PrimaryUrl": "$url", "Checksum": "$checksum" } }
        """.trimIndent()
        val http = InMemoryModelHttpClient().put(url, body)
        LocalModelLoader(dir.toString(), registry, http).use { loader ->
            val path = loader.downloadModelAsync("embed")
            assertContentEquals(body, File(path).readBytes())
            assertTrue(loader.modelExists("embed"))
            assertEquals(File(dir.toFile(), "embed.bin").path, loader.getModelPath("embed"))
        }
    }

    @Test
    fun `LocalModelLoader rejects an unsupported model`(@TempDir dir: Path) = runTest {
        LocalModelLoader(dir.toString(), "{}").use { loader ->
            assertFailsWith<IllegalArgumentException> { loader.downloadModelAsync("nope") }
        }
    }

    @Test
    fun `LocalModelLoader refuses to single-file-download a bundle entry`(@TempDir dir: Path) = runTest {
        val registry = """
            { "b": { "Repo": "x/y", "BundleFiles": [ { "Name": "llm.mnn.weight", "Sha256": "aa", "SizeBytes": 1 } ] } }
        """.trimIndent()
        LocalModelLoader(dir.toString(), registry).use { loader ->
            assertFailsWith<IllegalStateException> { loader.downloadModelAsync("b") }
            // Bundle path resolves to per-model dir + anchor file.
            assertEquals(File(File(dir.toFile(), "b"), "llm.mnn.weight").path, loader.getModelPath("b"))
        }
    }

    @Test
    fun `LocalModelLoader modelExists is false when file missing`(@TempDir dir: Path) {
        val registry = """ { "m": { "FileName": "m.bin", "Checksum": "sha256:deadbeef" } } """
        LocalModelLoader(dir.toString(), registry).use { loader ->
            assertFalse(loader.modelExists("m"))
        }
    }

    @Test
    fun `LocalModelLoader checkForCriticalUpdate detects the marker`(@TempDir dir: Path) = runTest {
        val versionsUrl = "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt"
        val http = InMemoryModelHttpClient().putText(versionsUrl, "v1.2.3 [CRITICAL] security fix")
        LocalModelLoader(dir.toString(), "{}", http).use { loader ->
            assertTrue(loader.checkForCriticalUpdateAsync())
        }
        val http2 = InMemoryModelHttpClient().putText(versionsUrl, "v1.2.3 routine")
        LocalModelLoader(dir.toString(), "{}", http2).use { loader ->
            assertFalse(loader.checkForCriticalUpdateAsync())
        }
    }

    // ── LocalModelManager ────────────────────────────────────────────────────

    @Test
    fun `LocalModelManager downloads on cache miss then returns the path`(@TempDir dir: Path) = runTest {
        // A downloader that writes pytorch_model.bin into the target dir.
        val content = "weights".toByteArray()
        val downloader = object : IModelDownloader {
            override suspend fun downloadModelAsync(modelId: String, localPath: String) {
                File(localPath).mkdirs()
                File(localPath, "pytorch_model.bin").writeBytes(content)
            }

            override suspend fun downloadFromCandidatesAsync(
                candidateUrls: List<String>,
                localFilePath: String,
                progress: ((DownloadProgress) -> Unit)?,
            ): String = "test"
        }
        LocalModelManager(downloader, dir.toString()).use { mgr ->
            val path = mgr.getModelPathAsync("acme/model")
            assertTrue(File(path, "pytorch_model.bin").exists())
            // Sanitised id: "acme/model" → "acme_model".
            assertEquals(File(dir.toFile(), "acme_model").path, path)

            // Checksum verify against the real content.
            val checksum = MessageDigest.getInstance("SHA-256").digest(content)
            assertTrue(mgr.verifyModelAsync(path, checksum))
            assertFalse(mgr.verifyModelAsync(path, ByteArray(32)))
        }
    }

    @Test
    fun `LocalModelManager throws when model missing and no downloader configured`(@TempDir dir: Path) = runTest {
        LocalModelManager(modelRepositoryUrl = null, modelsDirectory = dir.toString()).use { mgr ->
            assertFailsWith<IllegalStateException> { mgr.getModelPathAsync("x") }
        }
    }

    @Test
    fun `LocalModelManager rejects a mismatched checksum on download`(@TempDir dir: Path) = runTest {
        val downloader = object : IModelDownloader {
            override suspend fun downloadModelAsync(modelId: String, localPath: String) {
                File(localPath).mkdirs()
                File(localPath, "pytorch_model.bin").writeBytes("real".toByteArray())
            }
            override suspend fun downloadFromCandidatesAsync(
                candidateUrls: List<String>, localFilePath: String, progress: ((DownloadProgress) -> Unit)?,
            ): String = "t"
        }
        LocalModelManager(downloader, dir.toString()).use { mgr ->
            assertFailsWith<java.io.IOException> {
                mgr.getModelPathAsync("m", ByteArray(32) { 1 })
            }
        }
    }

    // ── CircleEngine ─────────────────────────────────────────────────────────

    @Test
    fun `CircleEngine registers and retrieves modules by type`(@TempDir dir: Path) {
        LocalModelLoader(dir.toString(), "{}").use { loader ->
            val engine = CircleEngine(loader)
            assertTrue(engine.modelLoader === loader)
            assertFalse(engine.hasModule<String>())

            engine.registerModule("hello")
            assertTrue(engine.hasModule<String>())
            assertEquals("hello", engine.getModule<String>())
            assertNull(engine.getModule<Int>())

            engine.embeddingService = 123
            assertEquals(123, engine.embeddingService)
        }
    }

    // ── SafeModelHandle / PlatformInterop ────────────────────────────────────

    @Test
    fun `SafeModelHandle invokes the release callback exactly once on close`() {
        var released = 0
        var releasedToken = 0L
        val handle = SafeModelHandle(0x1234L) { token -> released++; releasedToken = token }
        assertFalse(handle.isInvalid)
        handle.close()
        handle.close() // idempotent
        assertEquals(1, released)
        assertEquals(0x1234L, releasedToken)
        assertTrue(handle.isInvalid)
    }

    @Test
    fun `SafeModelHandle default constructor is invalid until set`() {
        val handle = SafeModelHandle()
        assertTrue(handle.isInvalid)
        handle.setHandle(9L)
        assertFalse(handle.isInvalid)
    }

    @Test
    fun `PlatformInterop loadModel validates the path and delegates the native load`(@TempDir dir: Path) {
        assertFailsWith<IllegalArgumentException> { PlatformInterop.loadModel("  ") }
        assertFailsWith<java.io.FileNotFoundException> { PlatformInterop.loadModel(File(dir.toFile(), "missing.gguf").path) }

        val model = File(dir.toFile(), "model.gguf")
        model.writeBytes(byteArrayOf(1, 2, 3))
        val handle = PlatformInterop.loadModel(model.path)
        assertFalse(handle.isInvalid)
        handle.close()

        // A loader that returns 0 signals failure.
        assertFailsWith<IllegalStateException> {
            PlatformInterop.loadModel(model.path) { 0L }
        }
    }

    // ── Auditing ─────────────────────────────────────────────────────────────

    @Test
    fun `NoopAuditLog discards entries and queries empty`() = runTest {
        val log = NoopAuditLog.Instance
        log.recordAsync(sampleEntry())
        val collected = mutableListOf<CircleAIAuditEntry>()
        log.queryAsync(CircleAIAuditQuery()).collect { collected.add(it) }
        assertTrue(collected.isEmpty())
    }

    @Test
    fun `LoggerAuditLog writes a structured line to the sink`() = runTest {
        val lines = mutableListOf<String>()
        val log = LoggerAuditLog { lines.add(it) }
        log.recordAsync(sampleEntry())
        assertEquals(1, lines.size)
        assertTrue(lines[0].contains("CircleAI audit MyComponent.DoThing success"))
        assertTrue(lines[0].contains("tenant=t1"))
    }

    @Test
    fun `CircleAIAuditing ambient default can be swapped and reset`() = runTest {
        assertTrue(CircleAIAuditing.default === NoopAuditLog.Instance)
        val lines = mutableListOf<String>()
        CircleAIAuditing.setDefault(LoggerAuditLog { lines.add(it) })
        CircleAIAuditing.default.recordAsync(sampleEntry())
        assertEquals(1, lines.size)
        CircleAIAuditing.resetToNoop()
        assertTrue(CircleAIAuditing.default === NoopAuditLog.Instance)
    }

    // ── Multi-tenant ─────────────────────────────────────────────────────────

    @Test
    fun `NullTenantContext throws on read and reports no tenant`() {
        val ctx = NullTenantContext.Instance
        assertFalse(ctx.hasTenant)
        assertFailsWith<IllegalStateException> { ctx.currentTenantId }
    }

    @Test
    fun `SingleTenantContext returns the fixed tenant`() {
        val ctx = SingleTenantContext("acme")
        assertTrue(ctx.hasTenant)
        assertEquals("acme", ctx.currentTenantId)
        assertFailsWith<IllegalArgumentException> { SingleTenantContext("  ") }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private fun sampleEntry() = CircleAIAuditEntry(
        at = java.time.OffsetDateTime.parse("2026-01-01T00:00:00Z"),
        component = "MyComponent",
        operation = "DoThing",
        outcome = "success",
        tenantId = "t1",
        durationMs = 12.5,
    )

    companion object {
        // ModelScopeSource probe URL (private in the source); duplicated here for
        // seeding the in-memory client's reachability probe.
        const val ModelScopeSource_PROBE = "https://modelscope.cn/"

        private val scratch: File by lazy {
            File(System.getProperty("java.io.tmpdir"), "circleai-core-test-" + System.nanoTime()).apply { mkdirs() }
        }

        fun tmpFile(name: String): String = File(scratch, "${System.nanoTime()}-$name").path
        fun tmpDir(name: String): String = File(scratch, "${System.nanoTime()}-$name").apply { mkdirs() }.path
    }
}
