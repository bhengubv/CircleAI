// ServerCoreTest.kt
//
// Verifies the ported CircleAI.Inference.Server core: registry, counters, native
// status, API-key auth, admission control, lifecycle manager (admission gate),
// the MNN bridge factory (in-memory), and the LocalProcessInferenceBridge.

package com.bhengubv.circleai.server

import com.bhengubv.circleai.embeddings.ITextEmbedder
import com.bhengubv.circleai.inference.IByteFetcher
import com.bhengubv.circleai.inference.LocalChatGenerator
import com.bhengubv.circleai.inference.ModelDownloadService
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.io.File
import java.nio.file.Files
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ServerCoreTest {

    private fun cpuGenBridge(modelId: String): IInferenceBridge {
        val descriptor = ModelDescriptor(
            modelId = modelId, version = "1", format = ModelFormat.Gguf,
            contextWindowTokens = 4096, vocabSize = 100, parameterCount = 0,
            quantisationLabel = null, approximateMemoryBytes = 0,
        )
        return LocalProcessInferenceBridge(LocalChatGenerator(modelId), descriptor)
    }

    // ── registry / counters / native status ──────────────────────────────────

    @Test
    fun `registry registers, resolves, lists and deregisters`() {
        val reg = InferenceServerModelRegistry()
        reg.register("qwen", cpuGenBridge("qwen"))
        reg.registerEmbedder("embed", object : ITextEmbedder {
            override suspend fun generateAsync(text: String) = floatArrayOf(1f)
        })
        assertNotNull(reg.resolve("qwen"))
        assertNotNull(reg.resolveEmbedder("embed"))
        assertEquals(listOf("qwen"), reg.chatModelIds())
        assertTrue(reg.allModelIds().containsAll(listOf("qwen", "embed")))
        assertTrue(reg.deregister("qwen"))
        assertNull(reg.resolve("qwen"))
    }

    @Test
    fun `counters account admitted, completed, rejected, failed`() {
        val c = ServerCounters()
        c.accountAdmitted(); c.accountAdmitted()
        assertEquals(2, c.totalRequests)
        assertEquals(2, c.activeRequests)
        c.accountCompleted()
        assertEquals(1, c.activeRequests)
        c.accountRejected(); c.accountFailed()
        assertEquals(1, c.rejectedRequests)
        assertEquals(1, c.failedRequests)
    }

    @Test
    fun `native runtime status holds the latest paths`() {
        val s = NativeRuntimeStatus()
        assertNull(s.latest)
        val paths = NativeRuntimePaths("rid", "d", "b", true, "f", "fl", true, null, null)
        s.update(paths)
        assertEquals(paths, s.latest)
    }

    // ── auth ─────────────────────────────────────────────────────────────────

    @Test
    fun `auth disabled succeeds anonymously`() {
        val opts = InferenceServerOptions(auth = AuthOptions(apiKey = ApiKeyOptions(enabled = false)))
        val handler = ApiKeyAuthHandler { opts }
        val r = handler.authenticate(emptyMap())
        assertTrue(r is AuthResult.Success)
        assertEquals("true", (r as AuthResult.Success).claims["auth_disabled"])
    }

    @Test
    fun `auth no header is NoResult, wrong key is Fail, right key is Success`() {
        val opts = InferenceServerOptions(
            auth = AuthOptions(apiKey = ApiKeyOptions(enabled = true, headerName = "X-Key", keys = listOf("secret-key"))),
        )
        val handler = ApiKeyAuthHandler { opts }
        assertTrue(handler.authenticate(emptyMap()) is AuthResult.NoResult)
        assertTrue(handler.authenticate(mapOf("X-Key" to "nope")) is AuthResult.Fail)
        // Header name is case-insensitive.
        assertTrue(handler.authenticate(mapOf("x-key" to "secret-key")) is AuthResult.Success)
    }

    // ── admission control ────────────────────────────────────────────────────

    @Test
    fun `admission control caps concurrency`() {
        val counters = ServerCounters()
        val ac = AdmissionControl(InferenceServerOptions(maxConcurrentRequests = 2), counters)
        val s1 = ac.tryEnter(); val s2 = ac.tryEnter()
        assertNotNull(s1); assertNotNull(s2)
        assertNull(ac.tryEnter()) // saturated
        s1!!.close()
        assertNotNull(ac.tryEnter()) // slot freed
        s2!!.close()
        assertEquals(1, counters.rejectedRequests)
    }

    // ── lifecycle manager ────────────────────────────────────────────────────

    @Test
    fun `lifecycle loads, is idempotent, and unloads`() = runTest {
        val reg = InferenceServerModelRegistry()
        val mgr = ModelLifecycleManager(reg, FixedCapabilityProbe.cpuHost())
        val descriptor = ModelLoadDescriptor(
            modelId = "qwen", backend = BackendKind.Cpu,
            requestedTier = CapabilityTier.Tier1_Small,
            vramRequiredBytes = 0, ramRequiredBytes = 1L * 1024 * 1024 * 1024,
            bridgeFactory = { cpuGenBridge("qwen") },
        )
        val r1 = mgr.loadAsync(descriptor)
        assertEquals(LoadOutcome.Loaded, r1.outcome)
        assertNotNull(reg.resolve("qwen"))

        val r2 = mgr.loadAsync(descriptor)
        assertEquals(LoadOutcome.AlreadyLoaded, r2.outcome)
        assertEquals(1, mgr.list().size)

        assertEquals(UnloadOutcome.Unloaded, mgr.unloadAsync("qwen"))
        assertEquals(UnloadOutcome.NotLoaded, mgr.unloadAsync("qwen"))
        assertNull(reg.resolve("qwen"))
    }

    @Test
    fun `lifecycle rejects on insufficient RAM and VRAM`() = runTest {
        val reg = InferenceServerModelRegistry()
        // Tiny host: 1 GB RAM, no GPU.
        val cpuMgr = ModelLifecycleManager(reg, FixedCapabilityProbe.cpuHost(totalRamBytes = 1L * 1024 * 1024 * 1024))
        val ramHog = ModelLoadDescriptor(
            "big", BackendKind.Cpu, CapabilityTier.Tier3_Large,
            0, 8L * 1024 * 1024 * 1024, { cpuGenBridge("big") },
        )
        assertEquals(LoadOutcome.InsufficientRam, cpuMgr.loadAsync(ramHog).outcome)

        // GPU host with small VRAM.
        val gpuMgr = ModelLifecycleManager(InferenceServerModelRegistry(), FixedCapabilityProbe.gpuHost(vramBytes = 2L * 1024 * 1024 * 1024))
        val vramHog = ModelLoadDescriptor(
            "gpu-big", BackendKind.Cuda, CapabilityTier.Tier4_Frontier,
            8L * 1024 * 1024 * 1024, 1L * 1024 * 1024 * 1024, { cpuGenBridge("gpu-big") },
        )
        assertEquals(LoadOutcome.InsufficientVram, gpuMgr.loadAsync(vramHog).outcome)
    }

    @Test
    fun `lifecycle rolls back reservation on factory failure`() = runTest {
        val reg = InferenceServerModelRegistry()
        val mgr = ModelLifecycleManager(reg, FixedCapabilityProbe.cpuHost())
        val bad = ModelLoadDescriptor(
            "boom", BackendKind.Cpu, CapabilityTier.Tier0_Tiny, 0, 0,
            { throw RuntimeException("kaboom") },
        )
        val r = mgr.loadAsync(bad)
        assertEquals(LoadOutcome.FactoryFailed, r.outcome)
        assertTrue(mgr.list().isEmpty()) // reservation rolled back
    }

    // ── bridge factory (in-memory) ───────────────────────────────────────────

    @Test
    fun `bridge factory materialises a working single-file bridge`() = runTest {
        val storage = Files.createTempDirectory("bf-models").toFile().absolutePath
        val url = "https://example/qwen.gguf"
        val fetcher = object : IByteFetcher {
            override suspend fun fetchToFileAsync(uri: String, dest: File, progress: ((Double) -> Unit)?) {
                dest.parentFile?.mkdirs(); dest.writeBytes("w".toByteArray()); progress?.invoke(1.0)
            }
        }
        val download = ModelDownloadService(storage, fetcher)
        val status = NativeRuntimeStatus()
        val factory = MnnInferenceBridgeFactory(
            probe = FixedCapabilityProbe.cpuHost(),
            registryLookup = { id -> if (id == "qwen") ServerModelEntry("qwen", url = url) else null },
            modelDownload = download,
            nativeStatus = status,
        )

        val bridge = factory.createAsync("qwen", BackendKind.Cpu, CapabilityTier.Tier1_Small)
        assertTrue(bridge.isModelLoadedAsync("qwen"))
        assertNotNull(status.latest) // native "prep" stamped
        val descr = bridge.listLoadedModelsAsync().single()
        assertEquals(4096, descr.contextWindowTokens)

        // The bridge actually completes.
        val resp = bridge.completeAsync(InferenceRequest.create("qwen", "hi"))
        assertEquals(InferenceStatus.Completed, resp.status)
        assertTrue(resp.outputText.isNotEmpty())
    }

    @Test
    fun `bridge factory fails fast for an unknown model`() = runTest {
        val storage = Files.createTempDirectory("bf-models2").toFile().absolutePath
        val factory = MnnInferenceBridgeFactory(
            probe = FixedCapabilityProbe.cpuHost(),
            registryLookup = { null },
            modelDownload = ModelDownloadService(storage, object : IByteFetcher {
                override suspend fun fetchToFileAsync(uri: String, dest: File, progress: ((Double) -> Unit)?) {}
            }),
        )
        try {
            factory.createAsync("ghost", BackendKind.Cpu, CapabilityTier.Tier0_Tiny)
            error("expected failure")
        } catch (e: IllegalStateException) {
            assertTrue(e.message!!.contains("ghost"))
        }
    }

    // ── LocalProcessInferenceBridge ──────────────────────────────────────────

    @Test
    fun `bridge reports failure for a model it does not host`() = runTest {
        val bridge = cpuGenBridge("qwen")
        val resp = bridge.completeAsync(InferenceRequest.create("other-model", "hi"))
        assertEquals(InferenceStatus.Failed, resp.status)
        assertNotNull(resp.failureMessage)
    }

    @Test
    fun `bridge streams at least one chunk`() = runTest {
        val bridge = cpuGenBridge("qwen")
        val chunks = ArrayList<String>()
        bridge.streamCompletionAsync(InferenceRequest.create("qwen", "hello")).collect { chunks.add(it) }
        assertTrue(chunks.isNotEmpty())
    }

    @Test
    fun `bridge device capabilities reflect the probe`() = runTest {
        val descriptor = ModelDescriptor("qwen", "1", ModelFormat.Gguf, 4096, 100, 0, null, 0)
        val bridge = LocalProcessInferenceBridge(
            LocalChatGenerator("qwen"), descriptor, FixedCapabilityProbe.gpuHost(),
        )
        val caps = bridge.getDeviceCapabilitiesAsync()
        assertTrue(caps.hasGpu)
        assertNotNull(caps.gpuMemoryBytes)
    }
}
