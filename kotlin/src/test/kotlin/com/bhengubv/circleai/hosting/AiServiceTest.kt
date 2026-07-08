// AiServiceTest.kt
//
// Verifies AIService + FallbackAIService against the C# reference: start/idempotence
// + warm-up gating, chat enrichment (persona/device-context system prompt
// injection + honouring a caller-supplied system message), observer events, tool
// invocation without a bridge, the agentic tool-call loop with Qwen <tool_call>
// parsing, feedback->persona verbosity adaptation, episodic writes, and fallback
// RAM-threshold routing. Also the ParseToolCall + observer bridges.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.device.IDeviceContext
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.EpisodicMemoryEntry
import com.bhengubv.circleai.memory.FeedbackPolarity
import com.bhengubv.circleai.memory.FeedbackSignal
import com.bhengubv.circleai.memory.IEpisodicMemoryStore
import com.bhengubv.circleai.memory.IFeedbackStore
import com.bhengubv.circleai.memory.IPersonaStore
import com.bhengubv.circleai.memory.PersonaState
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.tools.IToolBridge
import com.bhengubv.circleai.tools.ToolDefinition
import com.bhengubv.circleai.tools.ToolInvocation
import com.bhengubv.circleai.tools.ToolResult
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class AiServiceTest {

    /** Generator that records the exact messages it was handed, and replies scriptably. */
    private class RecordingGen(private val reply: (List<ChatMessage>) -> String) : IChatGenerator {
        val lastMessages = ArrayList<List<ChatMessage>>()
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
            lastMessages.add(messages)
            return reply(messages)
        }
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
            lastMessages.add(messages)
            emit(reply(messages))
        }
        override fun close() {}
    }

    private fun opts(
        observer: IAIObserver? = null,
        deviceContext: IDeviceContext? = null,
        personaStore: IPersonaStore? = null,
        feedbackStore: IFeedbackStore? = null,
        episodic: IEpisodicMemoryStore? = null,
        toolBridge: IToolBridge? = null,
        warmOnStart: Boolean = false,
        agenticMaxIterations: Int? = null,
    ) = AIOptions(
        modelId = "test-model",
        warmOnStart = warmOnStart,
        observer = observer,
        deviceContext = deviceContext,
        personaStore = personaStore,
        feedbackStore = feedbackStore,
        episodicMemory = episodic,
        toolBridge = toolBridge,
        agenticMaxIterations = agenticMaxIterations,
    )

    @Test
    fun `start is idempotent and warm-up runs only when enabled`() = runTest {
        val gen = RecordingGen { "ok" }
        var built = 0
        val svc = AIService(opts(warmOnStart = true), { built++; gen })
        svc.startAsync()
        svc.startAsync()
        assertEquals(1, built)
        assertTrue(svc.isReady)
        // Warm-up hands a system + "." user message to the generator.
        assertTrue(gen.lastMessages.any { msgs -> msgs.any { it.role == "user" && it.content == "." } })
        svc.disposeAsync()
    }

    @Test
    fun `ask injects the configured system prompt`() = runTest {
        val gen = RecordingGen { "reply" }
        val svc = AIService(AIOptions(modelId = "m", warmOnStart = false, systemPrompt = "SYS-PROMPT"), { gen })
        val answer = svc.askAsync("hello")
        assertEquals("reply", answer)
        val prepared = gen.lastMessages.last()
        assertEquals("system", prepared.first().role)
        assertTrue(prepared.first().content.startsWith("SYS-PROMPT"))
        assertEquals("hello", prepared.last().content)
        svc.disposeAsync()
    }

    @Test
    fun `caller-supplied system message is honoured verbatim`() = runTest {
        val gen = RecordingGen { "reply" }
        val svc = AIService(AIOptions(modelId = "m", warmOnStart = false, systemPrompt = "DEFAULT"), { gen })
        val custom = listOf(
            ChatMessage(UUID.randomUUID().toString(), "system", "CUSTOM"),
            ChatMessage(UUID.randomUUID().toString(), "user", "hi"),
        )
        svc.chatAsync(custom)
        val prepared = gen.lastMessages.last()
        assertEquals(2, prepared.size)
        assertEquals("CUSTOM", prepared.first().content)
        svc.disposeAsync()
    }

    @Test
    fun `device context is injected into the system prompt`() = runTest {
        val gen = RecordingGen { "reply" }
        val ctx = object : IDeviceContext {
            override val activeAppId = "NotesApp"
            override val networkType = "wifi"
        }
        val svc = AIService(opts(deviceContext = ctx), { gen })
        svc.askAsync("q")
        val sys = gen.lastMessages.last().first().content
        assertTrue(sys.contains("[Device context]"))
        assertTrue(sys.contains("Active app: NotesApp"))
        assertTrue(sys.contains("Network: wifi"))
        svc.disposeAsync()
    }

    @Test
    fun `chat fires the chat-completed observer event`() = runTest {
        val gen = RecordingGen { "the-reply" }
        val events = ArrayList<AIChatEvent>()
        val observer = object : AIObserverBase() {
            override suspend fun onChatCompletedAsync(event: AIChatEvent) { events.add(event) }
        }
        val svc = AIService(opts(observer = observer), { gen })
        svc.askAsync("q")
        assertEquals(1, events.size)
        assertEquals("the-reply", events.first().response)
        svc.disposeAsync()
    }

    @Test
    fun `stream fires start + complete observer events and yields tokens`() = runTest {
        val gen = RecordingGen { "streamed" }
        val started = ArrayList<AIStreamEvent>()
        val completed = ArrayList<AIStreamEvent>()
        val observer = object : AIObserverBase() {
            override suspend fun onStreamStartedAsync(event: AIStreamEvent) { started.add(event) }
            override suspend fun onStreamCompletedAsync(event: AIStreamEvent) { completed.add(event) }
        }
        val svc = AIService(opts(observer = observer), { gen })
        val chunks = svc.streamAsync(listOf(ChatMessage(UUID.randomUUID().toString(), "user", "q"))).toList()
        assertEquals(listOf("streamed"), chunks)
        assertEquals(1, started.size)
        assertEquals(1, completed.size)
        assertEquals(1, completed.first().tokenCount)
        svc.disposeAsync()
    }

    @Test
    fun `invoke tool without a bridge returns a failure result`() = runTest {
        val gen = RecordingGen { "x" }
        val svc = AIService(opts(), { gen })
        val result = svc.invokeToolAsync(ToolInvocation("t", emptyMap()))
        assertFalse(result.success)
        assertEquals("No tool bridge configured.", result.error)
        svc.disposeAsync()
    }

    @Test
    fun `agentic loop executes an embedded tool call then returns plain text`() = runTest {
        val bridge = object : IToolBridge {
            override val availableTools: List<ToolDefinition> = emptyList()
            var calls = 0
            override suspend fun invokeAsync(invocation: ToolInvocation): ToolResult {
                calls++
                return ToolResult.ok(invocation.toolName, "42")
            }
        }
        // First generation emits a tool call; second (after the tool result) is plain text.
        var turn = 0
        val gen = RecordingGen {
            if (turn++ == 0) "let me check <tool_call>{\"name\":\"calc\",\"arguments\":{\"x\":\"1\"}}</tool_call>"
            else "the answer is 42"
        }
        val svc = AIService(opts(toolBridge = bridge, agenticMaxIterations = 5), { gen })
        val out = svc.agenticChatAsync("compute")
        assertEquals("the answer is 42", out)
        assertEquals(1, bridge.calls)
        svc.disposeAsync()
    }

    @Test
    fun `feedback adapts persona verbosity toward brief on negatives`() = runTest {
        val persona = PersonaState("default")
        val store = object : IPersonaStore {
            override suspend fun loadAsync(userId: String) = persona
            override suspend fun saveAsync(persona: PersonaState) {}
        }
        val signals = ArrayList<FeedbackSignal>()
        val feedback = object : IFeedbackStore {
            override suspend fun save(signal: FeedbackSignal) { signals.add(signal) }
            override suspend fun getRecent(userId: String, limit: Int) = signals.takeLast(limit)
        }
        val gen = RecordingGen { "x" }
        val svc = AIService(opts(personaStore = store, feedbackStore = feedback), { gen })

        // Three negative signals -> verbosity "balanced" -> "brief".
        repeat(3) {
            svc.submitFeedbackAsync(FeedbackSignal(UUID.randomUUID().toString(), "default", "turn$it", FeedbackPolarity.Negative))
        }
        assertEquals("brief", persona.verbosity)
        assertEquals(3, persona.negativeSignals)
        svc.disposeAsync()
    }

    @Test
    fun `episodic memory records each exchange`() = runTest {
        val stored = ArrayList<EpisodicMemoryEntry>()
        val episodic = object : IEpisodicMemoryStore {
            override suspend fun save(entry: EpisodicMemoryEntry) { stored.add(entry) }
            override suspend fun getRecent(userId: String, limit: Int) = stored.takeLast(limit)
            override suspend fun delete(id: String) {}
        }
        val gen = RecordingGen { "reply-text" }
        val svc = AIService(opts(episodic = episodic), { gen })
        svc.askAsync("my question")
        assertEquals(1, stored.size)
        assertTrue(stored.first().content.contains("my question"))
        assertTrue(stored.first().content.contains("reply-text"))
        svc.disposeAsync()
    }

    @Test
    fun `fallback routes to cloud below the ram threshold and local above`() = runTest {
        val local = FakeAIService(replyFor = { "local:$it" })
        val cloud = FakeAIService(replyFor = { "cloud:$it" })

        val below = FallbackAIService(local, cloud, ramThresholdBytes = 1_000, availableRamProvider = { 500 })
        below.startAsync()
        assertEquals("cloud:hi", below.askAsync("hi"))

        val local2 = FakeAIService(replyFor = { "local:$it" })
        val cloud2 = FakeAIService(replyFor = { "cloud:$it" })
        val above = FallbackAIService(local2, cloud2, ramThresholdBytes = 1_000, availableRamProvider = { 5_000 })
        above.startAsync()
        assertEquals("local:hi", above.askAsync("hi"))
    }

    @Test
    fun `fallback falls through to cloud when local start throws`() = runTest {
        val local = object : IAIService by FakeAIService() {
            override suspend fun startAsync() { throw RuntimeException("no model") }
        }
        val cloud = FakeAIService(replyFor = { "cloud:$it" })
        val fb = FallbackAIService(local, cloud, ramThresholdBytes = 1_000, availableRamProvider = { 5_000 })
        fb.startAsync()
        assertEquals("cloud:x", fb.askAsync("x"))
    }
}
