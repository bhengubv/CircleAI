// WorldModelTest.kt
//
// Verifies FrequencyWorldModel and BayesianWorldModel against the C# reference
// semantics: observation extraction from scenario JSON, frequency tallying,
// Naive-Bayes posterior + softmax, case-insensitive matching with original-case
// retrieval, and the empty-evidence "unknown"/0.5 fallback.

package com.bhengubv.circleai.reasoning

import com.bhengubv.circleai.companion.reasoning.BayesianWorldModel
import com.bhengubv.circleai.companion.reasoning.FrequencyWorldModel
import com.bhengubv.circleai.companion.reasoning.extractObservations
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class WorldModelTest {

    // ── observation extraction ───────────────────────────────────────────────────

    @Test
    fun `extractObservations renders values exactly like dotnet JsonElement toString`() {
        // Booleans capitalise (True/False), null -> "", string unquoted, number raw,
        // object/array minified — matching .NET JsonElement.ToString().
        val obs = extractObservations(
            """{"s":"rain","n":5,"f":3.5,"bt":true,"bf":false,"nl":null,"obj":{"a":1},"arr":[1,2]}""",
        )
        assertEquals(
            listOf("s=rain", "n=5", "f=3.5", "bt=True", "bf=False", "nl=", "obj={\"a\":1}", "arr=[1,2]"),
            obs,
        )
    }

    @Test
    fun `extractObservations returns empty for blank, non-object, or invalid json`() {
        assertEquals(emptyList(), extractObservations(""))
        assertEquals(emptyList(), extractObservations("   "))
        assertEquals(emptyList(), extractObservations("[1,2,3]"))
        assertEquals(emptyList(), extractObservations("not json"))
        assertEquals(emptyList(), extractObservations("null"))
    }

    // ── FrequencyWorldModel ───────────────────────────────────────────────────────

    @Test
    fun `frequency picks the most frequent outcome and reports probability`() = runTest {
        val m = FrequencyWorldModel()
        // obs "weather=rain" -> umbrella x3, wet x1
        repeat(3) { m.observe(listOf("weather=rain"), "umbrella") }
        m.observe(listOf("weather=rain"), "wet")

        val p = m.predictAsync("""{"weather":"rain"}""")
        assertEquals("umbrella", p.outcome)
        assertEquals(0.75, p.probability, 1e-12)
        assertEquals(listOf("weather=rain"), p.supportingFactors)
    }

    @Test
    fun `frequency returns unknown at 0_5 when no evidence matches`() = runTest {
        val m = FrequencyWorldModel()
        m.observe(listOf("weather=rain"), "umbrella")
        val p = m.predictAsync("""{"weather":"sunny"}""")
        assertEquals("unknown", p.outcome)
        assertEquals(0.5, p.probability, 0.0)
        assertTrue(p.supportingFactors.isEmpty())
    }

    @Test
    fun `frequency matches observations case-insensitively but keeps original outcome casing`() = runTest {
        val m = FrequencyWorldModel()
        m.observe(listOf("Weather=Rain"), "Umbrella")
        // scenario yields "weather=rain" (lower) — must still match the stored obs.
        val p = m.predictAsync("""{"weather":"rain"}""")
        assertEquals("Umbrella", p.outcome, "original outcome spelling is retained")
        assertEquals(1.0, p.probability, 0.0)
    }

    @Test
    fun `frequency aggregates across multiple matching observations`() = runTest {
        val m = FrequencyWorldModel()
        m.observe(listOf("a=1"), "X")
        m.observe(listOf("b=2"), "X")
        m.observe(listOf("b=2"), "Y")
        val p = m.predictAsync("""{"a":1,"b":2}""")
        // X: 1 (from a) + 1 (from b) = 2; Y: 1. total 3.
        assertEquals("X", p.outcome)
        assertEquals(2.0 / 3.0, p.probability, 1e-12)
        assertEquals(listOf("a=1", "b=2"), p.supportingFactors)
    }

    @Test
    fun `frequency observe rejects blank outcome`() {
        val m = FrequencyWorldModel()
        assertFailsWith<IllegalArgumentException> { m.observe(listOf("a=1"), "  ") }
    }

    // ── BayesianWorldModel ──────────────────────────────────────────────────────

    @Test
    fun `bayes returns unknown at 0_5 before any evidence`() = runTest {
        val m = BayesianWorldModel()
        val p = m.predictAsync("""{"weather":"rain"}""")
        assertEquals("unknown", p.outcome)
        assertEquals(0.5, p.probability, 0.0)
        assertTrue(p.supportingFactors.isEmpty())
    }

    @Test
    fun `bayes returns unknown at 0_5 when scenario has no observations`() = runTest {
        val m = BayesianWorldModel()
        m.observe(listOf("weather=rain"), "umbrella")
        val p = m.predictAsync("{}")
        assertEquals("unknown", p.outcome)
        assertEquals(0.5, p.probability, 0.0)
    }

    @Test
    fun `bayes favours the outcome whose likelihood best fits the observations`() = runTest {
        val m = BayesianWorldModel()
        repeat(5) { m.observe(listOf("weather=rain"), "umbrella") }
        repeat(5) { m.observe(listOf("weather=sunny"), "sunscreen") }

        val p = m.predictAsync("""{"weather":"rain"}""")
        assertEquals("umbrella", p.outcome)
        assertTrue(p.probability > 0.5, "posterior should favour umbrella; was ${p.probability}")
        assertEquals(listOf("weather=rain"), p.supportingFactors)
    }

    @Test
    fun `bayes posterior over two outcomes matches the hand-computed softmax`() = runTest {
        // One example each -> priors equal; single observation each.
        // vocabSize = 2 (rain, sunny), totalEx = 2, numOutcomes = 2, alpha = 1.
        val m = BayesianWorldModel(laplaceAlpha = 1.0)
        m.observe(listOf("weather=rain"), "umbrella")
        m.observe(listOf("weather=sunny"), "sunscreen")

        val p = m.predictAsync("""{"weather":"rain"}""")

        // Hand computation:
        //   logPrior(each) = ln((1+1)/(2+1*2)) = ln(0.5)
        //   umbrella: cond{rain:1}, totalForOutcome=1 -> P(rain)=(1+1)/(1+1*2)=2/3
        //   sunscreen: cond{sunny:1}, totalForOutcome=1 -> P(rain)=(0+1)/(1+1*2)=1/3
        //   logPost_umb = ln .5 + ln(2/3); logPost_sun = ln .5 + ln(1/3)
        //   softmax(umbrella) = (2/3) / (2/3 + 1/3) = 2/3.
        assertEquals("umbrella", p.outcome)
        assertEquals(2.0 / 3.0, p.probability, 1e-9)
    }

    @Test
    fun `bayes constructor rejects non-positive alpha`() {
        assertFailsWith<IllegalArgumentException> { BayesianWorldModel(laplaceAlpha = 0.0) }
        assertFailsWith<IllegalArgumentException> { BayesianWorldModel(laplaceAlpha = -1.0) }
    }

    @Test
    fun `bayes observe rejects blank outcome`() {
        val m = BayesianWorldModel()
        assertFailsWith<IllegalArgumentException> { m.observe(listOf("a=1"), "") }
    }
}
