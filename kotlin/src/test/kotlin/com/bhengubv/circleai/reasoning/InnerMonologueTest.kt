// InnerMonologueTest.kt
//
// Verifies TemplateInnerMonologue and ReasoningLoopInnerMonologue against the
// C# reference semantics: the three narrative frames, {summary}/{direction}
// substitution, the 12-word summary truncation, keyword direction inference,
// deterministic frame selection, and the reasoning-loop's reasoning-over-content
// preference with graceful stream-failure fallback.

package com.bhengubv.circleai.reasoning

import com.bhengubv.circleai.companion.reasoning.ReasoningLoopInnerMonologue
import com.bhengubv.circleai.companion.reasoning.TemplateInnerMonologue
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatFragment
import com.bhengubv.circleai.models.ChatFragmentKind
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class InnerMonologueTest {

    // ── TemplateInnerMonologue ────────────────────────────────────────────────────

    private val frames = listOf(
        "Observation: {s}. Implication: this likely means {d}.",
        "Looking at {s}, the salient pattern is {d}.",
        "Given {s}, my next step is to {d}.",
    )

    private fun matchesAFrame(thought: String, summary: String, direction: String): Boolean =
        frames.any { thought == it.replace("{s}", summary).replace("{d}", direction) }

    @Test
    fun `template renders one of the three frames with summary and direction`() = runTest {
        val mono = TemplateInnerMonologue()
        val ctx = """{"user":"asks about weather"}"""
        val r = mono.reflectAsync(ctx)
        // "user" keyword -> "respond to the user"; summary is the de-punctuated JSON.
        // {"user":"asks about weather"} -> tokens: user : asks about weather
        val summary = "user : asks about weather"
        assertTrue(
            matchesAFrame(r.thought, summary, "respond to the user"),
            "unexpected thought: ${r.thought}",
        )
    }

    @Test
    fun `template infers direction from keywords in priority order`() = runTest {
        val mono = TemplateInnerMonologue()
        assertTrue(mono.reflectAsync("""{"error":"boom"}""").thought.contains("diagnose the failure first"))
        assertTrue(mono.reflectAsync("""{"goal":"ship"}""").thought.contains("advance toward the stated goal"))
        assertTrue(mono.reflectAsync("""{"user":"hi"}""").thought.contains("respond to the user"))
        assertTrue(mono.reflectAsync("""{"weather":"rain"}""").thought.contains("gather more context"))
        // "error" wins over "goal" when both present (checked first).
        assertTrue(mono.reflectAsync("""{"goal":"x","error":"y"}""").thought.contains("diagnose the failure first"))
    }

    @Test
    fun `template summary keeps only the first twelve words`() = runTest {
        val mono = TemplateInnerMonologue()
        val ctx = "one two three four five six seven eight nine ten eleven twelve thirteen fourteen"
        val r = mono.reflectAsync(ctx)
        assertTrue(r.thought.contains("one two three four five six seven eight nine ten eleven twelve"))
        assertFalse(r.thought.contains("thirteen"))
    }

    @Test
    fun `template frame selection is deterministic for the same context`() = runTest {
        val mono = TemplateInnerMonologue()
        val ctx = """{"goal":"deterministic"}"""
        val a = mono.reflectAsync(ctx).thought
        val b = mono.reflectAsync(ctx).thought
        assertEquals(a, b)
    }

    // ── ReasoningLoopInnerMonologue ───────────────────────────────────────────────

    /** A fake generator that emits a scripted list of fragments, then completes. */
    private class ScriptedGenerator(private val fragments: List<ChatFragment>) : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String =
            fragments.filter { it.kind == ChatFragmentKind.CONTENT }.joinToString("") { it.text }

        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
            fragments.filter { it.kind == ChatFragmentKind.CONTENT }.forEach { emit(it.text) }
        }

        override fun streamFragmentsAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<ChatFragment> =
            flow { fragments.forEach { emit(it) } }

        override fun close() {}
    }

    /** A fake generator whose fragment stream throws partway through. */
    private class ThrowingGenerator(private val preface: List<ChatFragment>) : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String = ""
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {}
        override fun streamFragmentsAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<ChatFragment> =
            flow {
                preface.forEach { emit(it) }
                throw RuntimeException("stream exploded")
            }
        override fun close() {}
    }

    @Test
    fun `reasoning-loop prefers the reasoning trace as the thought`() = runTest {
        val gen = ScriptedGenerator(
            listOf(
                ChatFragment(ChatFragmentKind.REASONING, "The user seems tired; "),
                ChatFragment(ChatFragmentKind.REASONING, "I should be gentle."),
                ChatFragment(ChatFragmentKind.CONTENT, "Take it easy."),
            ),
        )
        val mono = ReasoningLoopInnerMonologue(gen)
        val r = mono.reflectAsync("""{"mood":"low"}""")
        assertEquals("The user seems tired; I should be gentle.", r.thought)
    }

    @Test
    fun `reasoning-loop falls back to content when there is no reasoning`() = runTest {
        val gen = ScriptedGenerator(
            listOf(ChatFragment(ChatFragmentKind.CONTENT, "  Just an observation.  ")),
        )
        val mono = ReasoningLoopInnerMonologue(gen)
        val r = mono.reflectAsync("{}")
        assertEquals("Just an observation.", r.thought) // trimmed
    }

    @Test
    fun `reasoning-loop yields no-inner-state when nothing is emitted`() = runTest {
        val mono = ReasoningLoopInnerMonologue(ScriptedGenerator(emptyList()))
        val r = mono.reflectAsync("{}")
        assertEquals("(no inner state)", r.thought)
    }

    @Test
    fun `reasoning-loop swallows a stream failure and keeps what it accumulated`() = runTest {
        val gen = ThrowingGenerator(
            listOf(ChatFragment(ChatFragmentKind.REASONING, "partial thought")),
        )
        val mono = ReasoningLoopInnerMonologue(gen)
        val r = mono.reflectAsync("{}")
        assertEquals("partial thought", r.thought)
    }
}
