// CloudFallbackTest.kt
//
// Verifies CircleAI.Hosting.CloudFallback against the C# reference:
// CloudFallbackChain skipping unconfigured/fail-soft generators and committing to
// the first that yields a real frame; BackupBrainOrchestrator degraded/cooldown
// failover with a driven clock; the OpenAI/Anthropic/Gemini SSE delta parsing via
// a fake transport; ServerSentEventsReader [DONE] handling; and the deterministic
// LocalFakeChatGenerator.

package com.bhengubv.circleai.hosting.cloudfallback

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CloudFallbackTest {

    private fun user(text: String) = listOf(ChatMessage(UUID.randomUUID().toString(), "user", text))

    /** A configurable generator that either declines (fail-soft), throws, or replies. */
    private class ScriptedGen(
        override val engineLabel: String,
        override val isConfigured: Boolean = true,
        private val reply: String? = null,
        private val throws: Boolean = false,
        private val failSoft: Boolean = false,
    ) : IConfigurableChatGenerator {
        override val statusMessage = engineLabel
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
            if (throws) throw RuntimeException("boom")
            if (failSoft) return "[$engineLabel not configured]"
            return reply ?: ""
        }
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
            if (throws) throw RuntimeException("boom")
            if (failSoft) { emit("[$engineLabel not configured]"); return@flow }
            reply?.let { emit(it) }
        }
        override fun close() {}
    }

    // ── CloudFallbackChain ──

    @Test
    fun `chain skips unconfigured and uses first ready generator`() = runTest {
        val chain = CloudFallbackChain(
            listOf(
                ScriptedGen("unconfigured", isConfigured = false, reply = "never"),
                ScriptedGen("cloud", reply = "from-cloud"),
            ),
        )
        assertEquals("from-cloud", chain.generateAsync(user("hi"), GenerationOptions()))
    }

    @Test
    fun `chain skips a fail-soft frame and moves to the next generator`() = runTest {
        val chain = CloudFallbackChain(
            listOf(
                ScriptedGen("declines", failSoft = true),
                ScriptedGen("cloud", reply = "real-answer"),
            ),
        )
        assertEquals(listOf("real-answer"), chain.streamAsync(user("hi"), GenerationOptions()).toList())
    }

    @Test
    fun `chain skips a throwing generator`() = runTest {
        val chain = CloudFallbackChain(
            listOf(
                ScriptedGen("faulty", throws = true),
                ScriptedGen("cloud", reply = "ok"),
            ),
        )
        assertEquals("ok", chain.generateAsync(user("hi"), GenerationOptions()))
    }

    @Test
    fun `chain reports failure when nothing serves`() = runTest {
        val chain = CloudFallbackChain(listOf(ScriptedGen("a", isConfigured = false)))
        assertTrue(chain.generateAsync(user("hi"), GenerationOptions()).contains("no configured generator"))
        assertEquals(listOf(CloudFallbackChain.NO_GENERATOR), chain.streamAsync(user("hi"), GenerationOptions()).toList())
    }

    // ── BackupBrainOrchestrator ──

    @Test
    fun `orchestrator falls over to the backup and marks the primary degraded`() = runTest {
        var now = Instant.parse("2026-07-08T00:00:00Z")
        val primary = ScriptedGen("primary", throws = true)
        val backup = ScriptedGen("backup", reply = "backup-answer")
        val orch = BackupBrainOrchestrator(
            listOf(primary, backup),
            BackupBrainPolicy(degradedAfterFailures = 2, maxRetriesPerTurn = 3),
            clock = { now },
        )

        // First turn: primary fails once (consecutive=1, still healthy), backup answers.
        assertEquals("backup-answer", orch.generateAsync(user("q"), GenerationOptions()))
        assertEquals(BrainHealth.Healthy, orch.statuses[0].health)

        // Second turn: primary fails again -> consecutive=2 -> degraded.
        assertEquals("backup-answer", orch.generateAsync(user("q"), GenerationOptions()))
        assertEquals(BrainHealth.Degraded, orch.statuses[0].health)

        // After the cool-down it goes half-open (CoolingDown).
        now = now.plus(Duration.ofSeconds(31))
        assertEquals(BrainHealth.CoolingDown, orch.statuses[0].health)
    }

    @Test
    fun `orchestrator reports all-failed when every brain throws`() = runTest {
        val orch = BackupBrainOrchestrator(
            listOf(ScriptedGen("a", throws = true), ScriptedGen("b", throws = true)),
            BackupBrainPolicy(maxRetriesPerTurn = 2),
        )
        assertEquals(BackupBrainOrchestrator.ALL_FAILED, orch.generateAsync(user("q"), GenerationOptions()))
    }

    @Test
    fun `orchestrator requires at least one brain`() {
        try {
            BackupBrainOrchestrator(emptyList())
            throw AssertionError("expected IllegalArgumentException")
        } catch (_: IllegalArgumentException) {
            // expected
        }
    }

    // ── SSE reader ──

    @Test
    fun `sse reader yields data frames and stops at DONE`() {
        val lines = listOf(
            "data: alpha",
            "event: ping",
            "data: beta",
            "data: [DONE]",
            "data: never",
        )
        assertEquals(listOf("alpha", "beta"), ServerSentEventsReader.readFrames(lines))
    }

    // ── Cloud generators over a fake transport ──

    private class FakeTransport(
        private val statusCode: Int = 200,
        private val lines: List<String> = emptyList(),
    ) : ICloudHttpTransport {
        var lastPath: String? = null
        var lastHeaders: Map<String, String> = emptyMap()
        var lastBody: String? = null
        override suspend fun postSse(
            baseAddress: String,
            path: String,
            headers: Map<String, String>,
            jsonBody: String,
        ): CloudHttpResponse {
            lastPath = path
            lastHeaders = headers
            lastBody = jsonBody
            return CloudHttpResponse(statusCode, lines)
        }
    }

    @Test
    fun `openai-compatible generator parses choices delta content`() = runTest {
        val transport = FakeTransport(
            lines = listOf(
                """data: {"choices":[{"delta":{"content":"Hel"}}]}""",
                """data: {"choices":[{"delta":{"content":"lo"}}]}""",
                "data: [DONE]",
            ),
        )
        val groq = GroqChatGenerator(transport, GroqChatOptions(apiKey = "k"))
        assertEquals("Hello", groq.generateAsync(user("hi"), GenerationOptions()))
        assertEquals("/openai/v1/chat/completions", transport.lastPath)
        assertEquals("Bearer k", transport.lastHeaders["Authorization"])
    }

    @Test
    fun `unconfigured cloud generator yields a fail-soft frame`() = runTest {
        val groq = GroqChatGenerator(FakeTransport(), GroqChatOptions(apiKey = null))
        val out = groq.streamAsync(user("hi"), GenerationOptions()).toList()
        assertEquals(1, out.size)
        assertTrue(out[0].contains("not configured"))
    }

    @Test
    fun `openai generator surfaces http errors`() = runTest {
        val transport = FakeTransport(statusCode = 429, lines = listOf("rate limited"))
        val gen = OpenAiChatGenerator(transport, OpenAiChatOptions(apiKey = "k"))
        val out = gen.generateAsync(user("hi"), GenerationOptions())
        assertTrue(out.contains("OpenAI error 429"))
    }

    @Test
    fun `anthropic generator parses content_block_delta and sends system out-of-band`() = runTest {
        val transport = FakeTransport(
            lines = listOf(
                """data: {"type":"content_block_delta","delta":{"text":"Hi"}}""",
                """data: {"type":"message_stop"}""",
            ),
        )
        val gen = AnthropicChatGenerator(transport, AnthropicChatOptions(apiKey = "k"))
        val messages = listOf(
            ChatMessage(UUID.randomUUID().toString(), "system", "be brief"),
            ChatMessage(UUID.randomUUID().toString(), "user", "hi"),
        )
        assertEquals("Hi", gen.generateAsync(messages, GenerationOptions()))
        assertTrue(transport.lastBody!!.contains("\"system\":\"be brief\""))
        assertEquals("k", transport.lastHeaders["x-api-key"])
    }

    @Test
    fun `gemini generator parses candidates parts text`() = runTest {
        val transport = FakeTransport(
            lines = listOf(
                """data: {"candidates":[{"content":{"parts":[{"text":"Ge"}]}}]}""",
                """data: {"candidates":[{"content":{"parts":[{"text":"mini"}]}}]}""",
            ),
        )
        val gen = GeminiChatGenerator(transport, GeminiChatOptions(apiKey = "k"))
        assertEquals("Gemini", gen.generateAsync(user("hi"), GenerationOptions()))
        assertTrue(transport.lastPath!!.contains("streamGenerateContent"))
    }

    // ── Local fake generator ──

    @Test
    fun `local fake echoes last user message and streams in chunks`() = runTest {
        val gen = LocalFakeChatGenerator(chunkSize = 4)
        assertEquals("hello", gen.generateAsync(user("hello"), GenerationOptions()))
        val chunks = gen.streamAsync(user("abcdefg"), GenerationOptions()).toList()
        assertEquals("abcdefg", chunks.joinToString(""))
        assertTrue(chunks.size >= 2)

        val fixed = LocalFakeChatGenerator(fixedReply = "canned")
        assertEquals("canned", fixed.generateAsync(user("whatever"), GenerationOptions()))
    }

    @Test
    fun `chain with local-first stays sovereign when local answers`() = runTest {
        val chain = CloudFallbackChain(
            listOf(
                LocalFakeChatGenerator(fixedReply = "on-device"),
                ScriptedGen("cloud", reply = "cloud"),
            ),
        )
        assertEquals("on-device", chain.generateAsync(user("hi"), GenerationOptions()))
    }
}
