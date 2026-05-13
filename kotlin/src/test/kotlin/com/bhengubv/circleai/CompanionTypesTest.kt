// CompanionTypesTest.kt
//
// Verifies:
//   - InterfaceKind has exactly 7 values with correct names
//   - CompanionContext, CompanionTurn, CompanionProactiveEvent data class construction
//   - PersonaState.toSystemPromptHint() against all 6 fixture vectors from persona_state.json

package com.bhengubv.circleai

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.memory.PersonaState
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import java.io.File
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CompanionTypesTest {

    private val json = Json { ignoreUnknownKeys = true }

    private fun locateFixture(name: String): File {
        val absolute = File("C:\\Dev\\Solutions\\com.bhengubv\\CircleAI\\fixtures\\$name")
        if (absolute.exists()) return absolute
        val relative = File("../../fixtures/$name")
        if (relative.exists()) return relative
        error("Cannot locate fixture $name")
    }

    // ── InterfaceKind ─────────────────────────────────────────────────────────

    @Test
    fun `InterfaceKind has exactly 7 values`() {
        assertEquals(7, InterfaceKind.entries.size,
            "InterfaceKind must have exactly 7 values, got ${InterfaceKind.entries.size}")
    }

    @Test
    fun `InterfaceKind values are correct`() {
        val names = InterfaceKind.entries.map { it.name }
        assertEquals(
            listOf("Mobile", "Wearable", "Desktop", "Web", "IoT", "Ambient", "Headless"),
            names
        )
    }

    @Test
    fun `InterfaceKind ordinals are 0 through 6`() {
        InterfaceKind.entries.forEachIndexed { index, kind ->
            assertEquals(index, kind.ordinal, "${kind.name} should have ordinal $index")
        }
    }

    // ── CompanionContext data class ────────────────────────────────────────────

    @Test
    fun `CompanionContext constructs correctly`() {
        val now = Instant.now()
        val ctx = CompanionContext(
            identityId           = "id-123",
            displayName          = "Test User",
            preferredLanguage    = "en",
            interfaceKind        = InterfaceKind.Mobile,
            personaHints         = "Keep responses brief.",
            affectSummary        = "[Affect state]\n",
            recentMemorySnippets = listOf("Yesterday we discussed Kotlin."),
            activeGoals          = listOf("Learn Kotlin"),
            contextBuiltAt       = now
        )
        assertEquals("id-123", ctx.identityId)
        assertEquals(InterfaceKind.Mobile, ctx.interfaceKind)
        assertEquals(1, ctx.recentMemorySnippets.size)
        assertEquals(1, ctx.activeGoals.size)
        assertEquals(now, ctx.contextBuiltAt)
    }

    @Test
    fun `CompanionContext copy works`() {
        val original = CompanionContext(
            identityId           = "id-1",
            displayName          = "Alice",
            preferredLanguage    = null,
            interfaceKind        = InterfaceKind.Web,
            personaHints         = "",
            affectSummary        = "",
            recentMemorySnippets = emptyList(),
            activeGoals          = emptyList(),
            contextBuiltAt       = Instant.EPOCH
        )
        val updated = original.copy(displayName = "Bob", interfaceKind = InterfaceKind.Desktop)
        assertEquals("Bob", updated.displayName)
        assertEquals(InterfaceKind.Desktop, updated.interfaceKind)
        assertEquals("id-1", updated.identityId)
    }

    // ── CompanionTurn data class ───────────────────────────────────────────────

    @Test
    fun `CompanionTurn constructs correctly`() {
        val ts = Instant.parse("2026-05-13T10:00:00Z")
        val turn = CompanionTurn(role = "user", content = "Hello, B!", timestamp = ts)
        assertEquals("user", turn.role)
        assertEquals("Hello, B!", turn.content)
        assertEquals(ts, turn.timestamp)
    }

    @Test
    fun `CompanionTurn assistant role works`() {
        val ts = Instant.now()
        val turn = CompanionTurn(role = "assistant", content = "Hello! How can I help?", timestamp = ts)
        assertEquals("assistant", turn.role)
    }

    // ── CompanionProactiveEvent data class ────────────────────────────────────

    @Test
    fun `CompanionProactiveEvent constructs correctly`() {
        val now = Instant.now()
        val event = CompanionProactiveEvent(
            sessionId   = "sess-abc",
            identityId  = "id-xyz",
            interfaceKind = InterfaceKind.Ambient,
            message     = "Time for your daily goal check-in!",
            triggerName = "goal_checkin",
            generatedAt = now
        )
        assertEquals("sess-abc", event.sessionId)
        assertEquals(InterfaceKind.Ambient, event.interfaceKind)
        assertEquals("goal_checkin", event.triggerName)
        assertEquals(now, event.generatedAt)
    }

    // ── PersonaState.toSystemPromptHint() against fixture vectors ─────────────

    @Test
    fun `PersonaState toSystemPromptHint matches all fixture vectors`() {
        val root = json.parseToJsonElement(locateFixture("persona_state.json").readText()).jsonObject
        val vectors = root["vectors"]!!.jsonArray

        assertEquals(6, vectors.size, "Expected 6 persona_state fixture vectors")

        vectors.forEach { element ->
            val v = element.jsonObject
            val id = v["id"]!!.jsonPrimitive.content
            val inp = v["input"]!!.jsonObject
            val expected = v["expectedHint"]!!.jsonPrimitive.content

            val persona = PersonaState("test-user").apply {
                verbosity       = inp["verbosity"]!!.jsonPrimitive.content
                formality       = inp["formality"]!!.jsonPrimitive.content
                preferredLocale = inp["preferredLocale"]!!.takeIf { it !is JsonNull }?.jsonPrimitive?.content
            }

            val actual = persona.toSystemPromptHint()
            assertEquals(expected, actual,
                "[$id] toSystemPromptHint() mismatch.\nExpected: ${expected.repr()}\nActual:   ${actual.repr()}")
        }
    }

    private fun String.repr(): String = "\"${replace("\n", "\\n")}\""

    // ── PersonaState additional checks ───────────────────────────────────────

    @Test
    fun `PersonaState satisfactionScore is null when fewer than 10 signals`() {
        val persona = PersonaState("u").apply {
            positiveSignals = 5
            negativeSignals = 4
        }
        assertEquals(null, persona.satisfactionScore)
    }

    @Test
    fun `PersonaState satisfactionScore computed correctly when 10 or more signals`() {
        val persona = PersonaState("u").apply {
            positiveSignals = 8
            negativeSignals = 2
        }
        val score = persona.satisfactionScore
        assertTrue(score != null)
        assertEquals(0.8, score, 1e-9)
    }
}
