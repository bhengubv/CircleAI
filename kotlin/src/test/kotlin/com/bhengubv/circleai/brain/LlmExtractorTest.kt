// LlmExtractorTest.kt
//
// Verifies LlmKnowledgeGraphExtractor: parses a clean JSON array of triples,
// tolerates prose/markdown-fence-wrapped JSON, defaults confidence when "c" is
// missing/invalid, clamps out-of-range confidence, skips objects with blank
// s/p/o, and returns [] on garbage / on an empty turn / on a failing generator.
// Mirrors the just-verified TS reference (tests/llm_extractor.test.ts) 1:1.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.brain.LlmKnowledgeGraphExtractor
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class LlmExtractorTest {

    /** Minimal fake IChatGenerator that returns a canned reply, records the messages. */
    private class FakeChatGenerator(private val reply: String) : IChatGenerator {
        @Volatile
        var lastMessages: List<ChatMessage> = emptyList()

        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
            lastMessages = messages
            return reply
        }

        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
            lastMessages = messages
            emit(reply)
        }

        override fun close() {}
    }

    /** A generator that always throws — exercises the graceful-degradation path. */
    private class ThrowingChatGenerator : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String =
            throw RuntimeException("model offline")

        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {}

        override fun close() {}
    }

    // ── clean JSON ────────────────────────────────────────────────────────────

    @Test
    fun `parses a plain JSON array of triples`() = runTest {
        val gen = FakeChatGenerator(
            "[{\"s\":\"Tony\",\"p\":\"has_daughter\",\"o\":\"Alex\",\"c\":0.9}," +
                "{\"s\":\"Alex\",\"p\":\"lives_in\",\"o\":\"Durban\",\"c\":0.5}]",
        )
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("hi", "ok", "ep1")

        assertEquals(2, triples.size)
        assertEquals("Tony", triples[0].subject)
        assertEquals("has_daughter", triples[0].predicate)
        assertEquals("Alex", triples[0].obj)
        assertEquals(0.9f, triples[0].confidence)
        assertEquals("ep1", triples[0].source)
        assertNotNull(triples[0].recordedAtUtc)
        assertEquals("Durban", triples[1].obj)
        assertEquals(0.5f, triples[1].confidence)
    }

    @Test
    fun `sends the verbatim system prompt plus USER-ASSISTANT-framed user message`() = runTest {
        val gen = FakeChatGenerator("[]")
        val ex = LlmKnowledgeGraphExtractor(gen)
        ex.extractFromTurnAsync("the weather", "is sunny", "ep1")

        assertEquals(2, gen.lastMessages.size)
        assertEquals("system", gen.lastMessages[0].role)
        assertTrue(gen.lastMessages[0].content.startsWith("You are a knowledge-graph extractor."))
        assertEquals("user", gen.lastMessages[1].role)
        assertEquals("USER:\nthe weather\nASSISTANT:\nis sunny\n", gen.lastMessages[1].content)
    }

    // ── defensive parsing ───────────────────────────────────────────────────────

    @Test
    fun `extracts JSON embedded in prose or markdown fences`() = runTest {
        val gen = FakeChatGenerator(
            "Sure! Here are the triples:\n```json\n[{\"s\":\"Paris\",\"p\":\"capital_of\",\"o\":\"France\",\"c\":0.95}]\n```\nHope that helps.",
        )
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("u", "a", "ep2")

        assertEquals(1, triples.size)
        assertEquals("Paris", triples[0].subject)
        assertEquals("capital_of", triples[0].predicate)
        assertEquals("France", triples[0].obj)
        assertEquals(0.95f, triples[0].confidence)
    }

    @Test
    fun `defaults confidence to 0_75 when c is missing`() = runTest {
        val gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]")
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("u", "a", "ep3")
        assertEquals(1, triples.size)
        assertEquals(0.75f, triples[0].confidence)
    }

    @Test
    fun `defaults confidence to 0_75 when c is non-numeric`() = runTest {
        val gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":\"high\"}]")
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("u", "a", "ep3")
        assertEquals(0.75f, triples[0].confidence)
    }

    @Test
    fun `clamps confidence into 0 to 1`() = runTest {
        val gen = FakeChatGenerator(
            "[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\",\"c\":5},{\"s\":\"d\",\"p\":\"e\",\"o\":\"f\",\"c\":-2}]",
        )
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("u", "a", "ep3")
        assertEquals(1f, triples[0].confidence)
        assertEquals(0f, triples[1].confidence)
    }

    @Test
    fun `skips objects whose s p o are blank or missing`() = runTest {
        val gen = FakeChatGenerator(
            "[{\"s\":\"\",\"p\":\"b\",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"  \",\"o\":\"c\"},{\"s\":\"a\",\"p\":\"b\"},{\"s\":\"keep\",\"p\":\"p\",\"o\":\"o\"}]",
        )
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("u", "a", "ep3")
        assertEquals(1, triples.size)
        assertEquals("keep", triples[0].subject)
    }

    @Test
    fun `skips non-object array entries`() = runTest {
        val gen = FakeChatGenerator("[1, \"two\", null, {\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]")
        val ex = LlmKnowledgeGraphExtractor(gen)
        val triples = ex.extractFromTurnAsync("u", "a", "ep3")
        assertEquals(1, triples.size)
        assertEquals("a", triples[0].subject)
    }

    // ── empty results ───────────────────────────────────────────────────────────

    @Test
    fun `returns empty on pure garbage with no brackets`() = runTest {
        val gen = FakeChatGenerator("I could not find any facts, sorry.")
        val ex = LlmKnowledgeGraphExtractor(gen)
        assertEquals(emptyList(), ex.extractFromTurnAsync("u", "a", "ep4"))
    }

    @Test
    fun `returns empty on malformed JSON inside brackets`() = runTest {
        val gen = FakeChatGenerator("[{\"s\":\"a\", \"p\": }]")
        val ex = LlmKnowledgeGraphExtractor(gen)
        assertEquals(emptyList(), ex.extractFromTurnAsync("u", "a", "ep4"))
    }

    @Test
    fun `returns empty when the JSON is an object not an array`() = runTest {
        val gen = FakeChatGenerator("{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}")
        val ex = LlmKnowledgeGraphExtractor(gen)
        // No '[' before ']' — object braces only, so no valid slice.
        assertEquals(emptyList(), ex.extractFromTurnAsync("u", "a", "ep4"))
    }

    @Test
    fun `returns empty when both user and assistant text are blank with no LLM call`() = runTest {
        val gen = FakeChatGenerator("[{\"s\":\"a\",\"p\":\"b\",\"o\":\"c\"}]")
        val ex = LlmKnowledgeGraphExtractor(gen)
        assertEquals(emptyList(), ex.extractFromTurnAsync("   ", "", null))
    }

    @Test
    fun `returns empty when the generator throws`() = runTest {
        val ex = LlmKnowledgeGraphExtractor(ThrowingChatGenerator())
        assertEquals(emptyList(), ex.extractFromTurnAsync("u", "a", "ep5"))
    }
}
