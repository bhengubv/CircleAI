// ServerHandlersTest.kt
//
// Verifies the in-memory endpoint handlers ported from the CircleAI.Inference.
// Server endpoints: chat completions (non-stream + stream + error mapping),
// embeddings (input normalisation + not-loaded), companion turn (send + agentic
// + missing field + session-not-found), and admin (load / unload / lifecycle +
// backend/tier validation). Also checks the OpenAI JSON wire shape.

package com.bhengubv.circleai.server

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.ICompanionSessionFactory
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.embeddings.ITextEmbedder
import com.bhengubv.circleai.inference.IByteFetcher
import com.bhengubv.circleai.inference.LocalChatGenerator
import com.bhengubv.circleai.inference.ModelDownloadService
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Test
import java.io.File
import java.nio.file.Files
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class ServerHandlersTest {

    private fun registryWithChat(modelId: String): InferenceServerModelRegistry {
        val reg = InferenceServerModelRegistry()
        val descriptor = ModelDescriptor(modelId, "1", ModelFormat.Gguf, 4096, 100, 0, null, 0)
        reg.register(modelId, LocalProcessInferenceBridge(LocalChatGenerator(modelId), descriptor))
        return reg
    }

    private fun deps(reg: InferenceServerModelRegistry): Triple<AdmissionControl, ServerCounters, InferenceServerModelRegistry> {
        val counters = ServerCounters()
        val admission = AdmissionControl(InferenceServerOptions(maxConcurrentRequests = 8), counters)
        return Triple(admission, counters, reg)
    }

    // ── chat completions ─────────────────────────────────────────────────────

    @Test
    fun `chat completion returns an assistant message`() = runTest {
        val reg = registryWithChat("qwen")
        val (adm, cnt, _) = deps(reg)
        val handler = ChatCompletionsHandler(reg, adm, cnt)
        val result = handler.handle(
            ChatCompletionRequest(model = "qwen", messages = listOf(ChatCompletionMessage(role = "user", content = "hello"))),
        )
        assertTrue(result.isSuccess)
        val body = result.body!!
        assertEquals("chat.completion", body.objectType)
        assertEquals("assistant", body.choices.single().message.role)
        assertTrue(body.choices.single().message.content.isNotEmpty())
        assertTrue(body.usage.totalTokens >= 0)
    }

    @Test
    fun `chat completion 400 on missing model and 404 on unloaded`() = runTest {
        val reg = registryWithChat("qwen")
        val (adm, cnt, _) = deps(reg)
        val handler = ChatCompletionsHandler(reg, adm, cnt)
        assertEquals(400, handler.handle(ChatCompletionRequest(model = "")).statusCode)
        assertEquals(
            400,
            handler.handle(ChatCompletionRequest(model = "qwen", messages = emptyList())).statusCode,
        )
        assertEquals(
            404,
            handler.handle(
                ChatCompletionRequest(model = "ghost", messages = listOf(ChatCompletionMessage(content = "x"))),
            ).statusCode,
        )
    }

    @Test
    fun `chat completion streaming yields role frame, deltas, stop, and DONE`() = runTest {
        val reg = registryWithChat("qwen")
        val (adm, cnt, _) = deps(reg)
        val handler = ChatCompletionsHandler(reg, adm, cnt)
        val result = handler.handle(
            ChatCompletionRequest(
                model = "qwen", stream = true,
                messages = listOf(ChatCompletionMessage(role = "user", content = "stream me")),
            ),
        )
        assertEquals(200, result.statusCode)
        val frames = result.streamFrames
        assertTrue(frames.isNotEmpty())
        // First frame announces the assistant role.
        val first = frames.first() as ChatCompletionStreamChunk
        assertEquals("assistant", first.choices.single().delta.role)
        // Last frame is the DONE sentinel.
        assertEquals(ChatCompletionsHandler.SSE_DONE, frames.last())
        // A stop frame exists.
        assertTrue(frames.filterIsInstance<ChatCompletionStreamChunk>().any { it.choices.single().finishReason == "stop" })
    }

    // ── embeddings ────────────────────────────────────────────────────────────

    @Test
    fun `embeddings returns one vector per input`() = runTest {
        val reg = InferenceServerModelRegistry()
        reg.registerEmbedder("embed", object : ITextEmbedder {
            override suspend fun generateAsync(text: String) = floatArrayOf(text.length.toFloat(), 0f)
        })
        val counters = ServerCounters()
        val handler = EmbeddingsHandler(reg, AdmissionControl(InferenceServerOptions(), counters), counters)

        val single = handler.handle(EmbeddingsRequest(model = "embed", single = "hello"))
        assertTrue(single.isSuccess)
        assertEquals(1, single.body!!.data.size)

        val many = handler.handle(EmbeddingsRequest(model = "embed", many = listOf("a", "bb", "ccc")))
        assertEquals(3, many.body!!.data.size)
        assertEquals(listOf(0, 1, 2), many.body!!.data.map { it.index })
    }

    @Test
    fun `embeddings 404 unloaded and 400 empty array`() = runTest {
        val reg = InferenceServerModelRegistry()
        reg.registerEmbedder("embed", object : ITextEmbedder {
            override suspend fun generateAsync(text: String) = floatArrayOf(0f)
        })
        val counters = ServerCounters()
        val handler = EmbeddingsHandler(reg, AdmissionControl(InferenceServerOptions(), counters), counters)
        assertEquals(404, handler.handle(EmbeddingsRequest(model = "ghost", single = "x")).statusCode)
        assertEquals(400, handler.handle(EmbeddingsRequest(model = "embed", many = emptyList())).statusCode)
        assertEquals(400, handler.handle(EmbeddingsRequest(model = "embed")).statusCode) // neither set
    }

    // ── companion turn ─────────────────────────────────────────────────────────

    /** Minimal fake session echoing the message; tracks history + agentic. */
    private class FakeSession(
        override val sessionId: String,
        override val identityId: String,
        override val interfaceKind: InterfaceKind = InterfaceKind.Web,
    ) : ICompanionSession {
        private val turns = ArrayList<CompanionTurn>()
        override val history: List<CompanionTurn> get() = turns
        override val proactiveEvents: Flow<CompanionProactiveEvent> get() = emptyFlow()

        override suspend fun sendAsync(message: String): String {
            turns.add(CompanionTurn("user", message, Instant.now()))
            return "echo: $message"
        }
        override fun streamAsync(message: String): Flow<String> = flow {
            emit("echo: "); emit(message)
        }
        override suspend fun agentAsync(instruction: String): String {
            turns.add(CompanionTurn("user", instruction, Instant.now()))
            return "agent: $instruction"
        }
        override fun getContext(): CompanionContext = CompanionContext(
            identityId, identityId, null, interfaceKind, "", "", emptyList(), emptyList(), Instant.now(),
        )
        override suspend fun refreshContextAsync() {}
        override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) {}
        override fun close() {}
    }

    private class FakeFactory : ICompanionSessionFactory {
        var creations = 0
        override suspend fun createAsync(identityId: String, interfaceKind: InterfaceKind): ICompanionSession {
            creations++
            return FakeSession("sess", identityId, interfaceKind)
        }
    }

    @Test
    fun `companion turn sends and increments turn index`() = runTest {
        val resolver = InMemoryCompanionSessionResolver(FakeFactory())
        val counters = ServerCounters()
        val handler = CompanionTurnHandler(resolver, AdmissionControl(InferenceServerOptions(), counters), counters)
        val r = handler.handle(CompanionTurnRequest(sessionId = "s", identityId = "u", message = "hi"))
        assertTrue(r.isSuccess)
        assertEquals("echo: hi", r.body!!.reply)
        assertEquals(1, r.body!!.turnIndex)
    }

    @Test
    fun `companion turn agentic path uses agentAsync`() = runTest {
        val resolver = InMemoryCompanionSessionResolver(FakeFactory())
        val counters = ServerCounters()
        val handler = CompanionTurnHandler(resolver, AdmissionControl(InferenceServerOptions(), counters), counters)
        val r = handler.handle(CompanionTurnRequest(sessionId = "s", identityId = "u", message = "plan", agentic = true))
        assertTrue(r.body!!.reply.startsWith("agent:"))
        assertTrue(r.body!!.agentic)
    }

    @Test
    fun `companion resolver single-flights one session per key`() = runTest {
        val factory = FakeFactory()
        val resolver = InMemoryCompanionSessionResolver(factory)
        val a = resolver.resolveAsync("s", "u")
        val b = resolver.resolveAsync("s", "u")
        assertTrue(a === b)
        assertEquals(1, factory.creations)
        assertEquals(1, resolver.cachedSessionCount)
    }

    @Test
    fun `companion turn 400 missing field and 404 unresolved session`() = runTest {
        val counters = ServerCounters()
        // Resolver that returns null (blank inputs) → 400 for missing message.
        val resolver = InMemoryCompanionSessionResolver(FakeFactory())
        val handler = CompanionTurnHandler(resolver, AdmissionControl(InferenceServerOptions(), counters), counters)
        assertEquals(400, handler.handle(CompanionTurnRequest(sessionId = "s", identityId = "u", message = "")).statusCode)

        // Resolver that always returns null → 404.
        val nullResolver = object : ICompanionSessionResolver {
            override suspend fun resolveAsync(sessionId: String, identityId: String): ICompanionSession? = null
        }
        val h2 = CompanionTurnHandler(nullResolver, AdmissionControl(InferenceServerOptions(), counters), counters)
        assertEquals(404, h2.handle(CompanionTurnRequest(sessionId = "s", identityId = "u", message = "hi")).statusCode)
    }

    // ── admin ───────────────────────────────────────────────────────────────

    @Test
    fun `admin load, lifecycle, and unload round-trip`() = runTest {
        val reg = InferenceServerModelRegistry()
        val mgr = ModelLifecycleManager(reg, FixedCapabilityProbe.cpuHost())
        val storage = Files.createTempDirectory("admin-models").toFile().absolutePath
        val download = ModelDownloadService(storage, object : IByteFetcher {
            override suspend fun fetchToFileAsync(uri: String, dest: File, progress: ((Double) -> Unit)?) {
                dest.parentFile?.mkdirs(); dest.writeBytes("w".toByteArray())
            }
        })
        val factory = MnnInferenceBridgeFactory(
            probe = FixedCapabilityProbe.cpuHost(),
            registryLookup = { id -> if (id == "qwen") ServerModelEntry("qwen", url = "https://e/q.gguf") else null },
            modelDownload = download,
        )
        val admin = AdminHandler(mgr, factory)

        val load = admin.load(AdminLoadRequest(modelId = "qwen", backend = "Cpu", tier = "Tier1_Small", ramRequiredBytes = 1024))
        assertTrue(load.isSuccess)
        assertEquals(LoadOutcome.Loaded, load.body!!.outcome)

        val life = admin.lifecycle()
        assertEquals(1, life.body!!.loaded.size)

        val unload = admin.unload("qwen")
        assertEquals("Unloaded", unload.body!!["outcome"])
        assertEquals(404, admin.unload("qwen").statusCode)
    }

    @Test
    fun `admin load rejects unknown backend and tier`() = runTest {
        val reg = InferenceServerModelRegistry()
        val mgr = ModelLifecycleManager(reg, FixedCapabilityProbe.cpuHost())
        val admin = AdminHandler(mgr, UnconfiguredBridgeFactory())
        assertEquals(400, admin.load(AdminLoadRequest(modelId = "x", backend = "Quantum")).statusCode)
        assertEquals(400, admin.load(AdminLoadRequest(modelId = "x", backend = "Cpu", tier = "Tier9")).statusCode)
        assertEquals(400, admin.load(AdminLoadRequest(modelId = "")).statusCode)
    }

    @Test
    fun `admin load surfaces factory failure as 500`() = runTest {
        val reg = InferenceServerModelRegistry()
        val mgr = ModelLifecycleManager(reg, FixedCapabilityProbe.cpuHost())
        val admin = AdminHandler(mgr, UnconfiguredBridgeFactory())
        val r = admin.load(AdminLoadRequest(modelId = "x", backend = "Cpu", tier = "Tier0_Tiny"))
        assertEquals(500, r.statusCode)
    }

    // ── wire shape ────────────────────────────────────────────────────────────

    @Test
    fun `chat completion response serialises to the OpenAI JSON shape`() {
        val json = Json { encodeDefaults = true }
        val resp = ChatCompletionResponse(
            id = "chatcmpl-1", created = 1, model = "qwen",
            choices = listOf(ChatCompletionChoice(0, ChatCompletionMessage(role = "assistant", content = "hi"), "stop")),
            usage = UsageInfo(1, 2, 3),
        )
        val text = json.encodeToString(ChatCompletionResponse.serializer(), resp)
        assertTrue(text.contains("\"object\":\"chat.completion\""))
        assertTrue(text.contains("\"finish_reason\":\"stop\""))
        assertTrue(text.contains("\"prompt_tokens\":1"))
        assertTrue(text.contains("\"total_tokens\":3"))
    }

    @Test
    fun `error response serialises with error envelope`() {
        val json = Json { encodeDefaults = true }
        val text = json.encodeToString(ErrorResponse.serializer(), ErrorResponse.of("bad", "invalid_request_error", "missing_model"))
        assertTrue(text.contains("\"error\""))
        assertTrue(text.contains("\"code\":\"missing_model\""))
    }
}
