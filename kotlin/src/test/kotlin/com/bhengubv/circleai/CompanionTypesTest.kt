// CompanionTypesTest.kt
//
// Verifies:
//   - InterfaceKind has exactly 7 values with correct names
//   - CompanionContext, CompanionTurn, CompanionProactiveEvent data class construction
//   - PersonaState.toSystemPromptHint() against all 6 fixture vectors from persona_state.json
//   - FaceAffectMapper.apply: Happy, Confused, low-confidence discard
//   - FaceCompanionBridge: confusion threshold triggers ProactiveEvent

package com.bhengubv.circleai

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.FaceAffectMapper
import com.bhengubv.circleai.companion.FaceCompanionBridge
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.memory.AffectState
import com.bhengubv.circleai.memory.PersonaState
import com.bhengubv.circleai.tools.FaceBoundingBox
import com.bhengubv.circleai.tools.FaceExpressionClassification
import com.bhengubv.circleai.tools.FacialMetricMatrix
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import java.io.File
import java.time.Instant
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun assertApprox(expected: Float, actual: Float, epsilon: Float = 1e-5f, message: String = "") {
        assertTrue(
            abs(actual - expected) <= epsilon,
            "${if (message.isNotEmpty()) "[$message] " else ""}expected $expected ± $epsilon, got $actual"
        )
    }

    /** Build a blank [FacialMetricMatrix] with zeroed landmarks and the given expression/confidence. */
    private fun matrix(
        expression: FaceExpressionClassification,
        confidence: Float
    ): FacialMetricMatrix = FacialMetricMatrix(
        landmarks      = FloatArray(136) { 0f },
        boundingBox    = FaceBoundingBox(0f, 0f, 1f, 1f),
        expression     = expression,
        confidenceScore = confidence,
        capturedAt     = Instant.EPOCH
    )

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
        assertNull(persona.satisfactionScore)
    }

    @Test
    fun `PersonaState satisfactionScore computed correctly when 10 or more signals`() {
        val persona = PersonaState("u").apply {
            positiveSignals = 8
            negativeSignals = 2
        }
        val score = persona.satisfactionScore
        assertNotNull(score)
        assertEquals(0.8, score, 1e-9)
    }

    // ── FaceAffectMapper — Happy expression ──────────────────────────────────

    @Test
    fun `FaceAffectMapper apply Happy increments engagement and energy`() {
        // fixtures: happy_from_neutral  engagement+0.03, energy+0.02
        val affect = AffectState("u").apply {
            curiosity   = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport     = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Happy, 0.92f), affect)
        assertApprox(0.5f,  affect.curiosity,   message = "curiosity unchanged")
        assertApprox(0.53f, affect.engagement,  message = "engagement +0.03")
        assertApprox(0.2f,  affect.uncertainty, message = "uncertainty unchanged")
        assertApprox(0.0f,  affect.rapport,     message = "rapport unchanged")
        assertApprox(0.52f, affect.energy,      message = "energy +0.02")
    }

    // ── FaceAffectMapper — Confused expression ────────────────────────────────

    @Test
    fun `FaceAffectMapper apply Confused increments uncertainty`() {
        // fixtures: confused_from_neutral  uncertainty+0.05
        val affect = AffectState("u").apply {
            curiosity   = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport     = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Confused, 0.79f), affect)
        assertApprox(0.5f,  affect.curiosity,   message = "curiosity unchanged")
        assertApprox(0.5f,  affect.engagement,  message = "engagement unchanged")
        assertApprox(0.25f, affect.uncertainty, message = "uncertainty +0.05")
        assertApprox(0.0f,  affect.rapport,     message = "rapport unchanged")
        assertApprox(0.5f,  affect.energy,      message = "energy unchanged")
    }

    // ── FaceAffectMapper — low confidence discard ─────────────────────────────

    @Test
    fun `FaceAffectMapper apply with confidence below 0_5 produces no change`() {
        // fixtures: low_confidence_discarded  confidence=0.49 → no mutation
        val affect = AffectState("u").apply {
            curiosity   = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport     = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Stressed, 0.49f), affect)
        assertApprox(0.5f, affect.curiosity,   message = "curiosity unchanged")
        assertApprox(0.5f, affect.engagement,  message = "engagement unchanged")
        assertApprox(0.2f, affect.uncertainty, message = "uncertainty unchanged")
        assertApprox(0.0f, affect.rapport,     message = "rapport unchanged")
        assertApprox(0.5f, affect.energy,      message = "energy unchanged")
    }

    @Test
    fun `FaceAffectMapper apply with confidence exactly 0_5 is applied`() {
        // MIN_CONFIDENCE = 0.5f, condition is < 0.5 so exactly 0.5 should mutate
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport   = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Confused, 0.5f), affect)
        // Confused adds 0.05 to uncertainty
        assertApprox(0.25f, affect.uncertainty, message = "uncertainty +0.05 at exactly min confidence")
    }

    // ── FaceAffectMapper — Surprised, Stressed, Angry ────────────────────────

    @Test
    fun `FaceAffectMapper apply Surprised increments curiosity`() {
        // fixtures: surprised_from_neutral  curiosity+0.04
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport   = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Surprised, 0.88f), affect)
        assertApprox(0.54f, affect.curiosity,  message = "curiosity +0.04")
        assertApprox(0.5f,  affect.engagement, message = "engagement unchanged")
        assertApprox(0.5f,  affect.energy,     message = "energy unchanged")
    }

    @Test
    fun `FaceAffectMapper apply Stressed increments uncertainty and decrements energy`() {
        // fixtures: stressed_from_neutral  uncertainty+0.08, energy-0.05
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport   = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Stressed, 0.85f), affect)
        assertApprox(0.28f, affect.uncertainty, message = "uncertainty +0.08")
        assertApprox(0.45f, affect.energy,      message = "energy -0.05")
    }

    @Test
    fun `FaceAffectMapper apply Angry decrements engagement and rapport`() {
        // fixtures: angry_from_neutral  engagement-0.04, rapport-0.02
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport   = 0.3f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Angry, 0.91f), affect)
        assertApprox(0.46f, affect.engagement, message = "engagement -0.04")
        assertApprox(0.28f, affect.rapport,    message = "rapport -0.02")
    }

    @Test
    fun `FaceAffectMapper apply Neutral produces no change`() {
        // fixtures: neutral_expression_no_change
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport   = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Neutral, 0.95f), affect)
        assertApprox(0.5f, affect.curiosity,   message = "curiosity unchanged")
        assertApprox(0.5f, affect.engagement,  message = "engagement unchanged")
        assertApprox(0.2f, affect.uncertainty, message = "uncertainty unchanged")
        assertApprox(0.0f, affect.rapport,     message = "rapport unchanged")
        assertApprox(0.5f, affect.energy,      message = "energy unchanged")
    }

    // ── FaceAffectMapper — clamp max ──────────────────────────────────────────

    @Test
    fun `FaceAffectMapper apply Happy clamps engagement at 1_0`() {
        // fixtures: clamp_max_engagement  engagement starts at 0.99
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.99f; uncertainty = 0.2f
            rapport   = 0.0f; energy     = 0.5f
        }
        FaceAffectMapper.apply(matrix(FaceExpressionClassification.Happy, 0.95f), affect)
        assertApprox(1.0f,  affect.engagement, message = "engagement clamped to 1.0")
        assertApprox(0.52f, affect.energy,     message = "energy +0.02")
    }

    // ── FaceCompanionBridge — confusion threshold ─────────────────────────────

    @Test
    fun `FaceCompanionBridge observe returns null below confusion threshold`() {
        // uncertainty starts at 0.2; Confused adds 0.05 → 0.25, well below 0.70
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.2f
            rapport   = 0.0f; energy     = 0.5f
        }
        val event = FaceCompanionBridge.observe(
            matrix  = matrix(FaceExpressionClassification.Confused, 0.79f),
            affect  = affect,
            sessionId  = "sess-1",
            identityId = "id-1",
            surface    = InterfaceKind.Mobile
        )
        assertNull(event, "Should not trigger below confusion threshold (0.25 < 0.70)")
    }

    @Test
    fun `FaceCompanionBridge observe returns event above confusion threshold`() {
        // uncertainty starts at 0.68; Confused adds 0.05 → 0.73 >= 0.70
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.68f
            rapport   = 0.0f; energy     = 0.5f
        }
        val event = FaceCompanionBridge.observe(
            matrix  = matrix(FaceExpressionClassification.Confused, 0.79f),
            affect  = affect,
            sessionId  = "sess-2",
            identityId = "id-2",
            surface    = InterfaceKind.Mobile
        )
        assertNotNull(event, "Should trigger when uncertainty crosses 0.70")
        assertEquals("sess-2",   event.sessionId)
        assertEquals("id-2",     event.identityId)
        assertEquals(InterfaceKind.Mobile, event.interfaceKind)
        assertEquals("face.confusion_detected", event.triggerName)
        assertTrue(event.message.isNotBlank())
    }

    @Test
    fun `FaceCompanionBridge observe Stressed also triggers above threshold`() {
        // uncertainty starts at 0.65; Stressed adds 0.08 → 0.73 >= 0.70
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.65f
            rapport   = 0.0f; energy     = 0.5f
        }
        val event = FaceCompanionBridge.observe(
            matrix  = matrix(FaceExpressionClassification.Stressed, 0.85f),
            affect  = affect,
            sessionId  = "sess-3",
            identityId = "id-3",
            surface    = InterfaceKind.Desktop
        )
        assertNotNull(event, "Stressed + high uncertainty should trigger event")
        assertEquals("face.confusion_detected", event.triggerName)
    }

    @Test
    fun `FaceCompanionBridge observe Happy does not trigger confusion event`() {
        // Happy never generates a confusion-expression, so no event even if uncertainty is high
        val affect = AffectState("u").apply {
            curiosity = 0.5f; engagement = 0.5f; uncertainty = 0.9f
            rapport   = 0.0f; energy     = 0.5f
        }
        val event = FaceCompanionBridge.observe(
            matrix  = matrix(FaceExpressionClassification.Happy, 0.92f),
            affect  = affect,
            sessionId  = "sess-4",
            identityId = "id-4",
            surface    = InterfaceKind.Mobile
        )
        assertNull(event, "Happy expression should never trigger confusion event")
    }

    @Test
    fun `FaceCompanionBridge confusion threshold constant is 0_70`() {
        assertEquals(0.70f, FaceCompanionBridge.CONFUSION_THRESHOLD)
    }
}
