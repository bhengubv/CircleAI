// SelfBenchSelfImprovementLoopTest.kt
//
// Verifies the SelfBench-backed self-improvement loop against the C# reference:
// an empty suite is skipped, a promoting verdict invokes onPromote and records
// the best score, and a rejecting verdict leaves the best untouched.

package com.bhengubv.circleai.companion

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class SelfBenchSelfImprovementLoopTest {

    private class NoopGenerator : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String = ""
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = emptyFlow()
        override fun close() {}
    }

    private fun registry(vararg suites: Pair<String, List<BenchTask>>): BenchSuiteRegistry =
        object : BenchSuiteRegistry {
            private val map = suites.toMap()
            override fun get(suiteId: String): List<BenchTask> = map[suiteId] ?: emptyList()
        }

    private fun runner(verdict: AbVerdict): AbBenchRunner = object : AbBenchRunner {
        override suspend fun compareAsync(
            suiteId: String,
            tasks: List<BenchTask>,
            baseline: IChatGenerator,
            candidate: IChatGenerator,
            gate: RegressionGateConfig,
        ): AbVerdict = verdict
    }

    @Test
    fun `empty suite is skipped`() = runTest {
        val loop = SelfBenchSelfImprovementLoop(
            registry = registry(),
            runner = runner(AbVerdict(AbSummary(0.9), AbSummary(0.5), true, "x")),
            baselineFactory = { NoopGenerator() },
            candidateFactory = { NoopGenerator() },
        )
        val verdict = loop.cycleAsync("missing")
        assertEquals("skipped: no tasks in suite", verdict.improvementsApplied)
        assertEquals(0.0, verdict.newBenchScore, 0.0)
    }

    @Test
    fun `promoting verdict invokes onPromote and records best score`() = runTest {
        var promoted = false
        val loop = SelfBenchSelfImprovementLoop(
            registry = registry("s" to listOf(BenchTask("t", "prompt"))),
            runner = runner(AbVerdict(AbSummary(0.92), AbSummary(0.80), shouldPromote = true, reason = "beat baseline")),
            baselineFactory = { NoopGenerator() },
            candidateFactory = { NoopGenerator() },
            onPromote = { promoted = true },
        )
        val verdict = loop.cycleAsync("s")
        assertTrue(promoted)
        assertTrue(verdict.improvementsApplied.startsWith("promoted candidate"))
        assertEquals(0.92, verdict.newBenchScore, 1e-12)
        assertEquals(0.92, loop.bestScoreFor("s"), 1e-12)
    }

    @Test
    fun `rejecting verdict leaves best untouched`() = runTest {
        var promoted = false
        val loop = SelfBenchSelfImprovementLoop(
            registry = registry("s" to listOf(BenchTask("t", "prompt"))),
            runner = runner(AbVerdict(AbSummary(0.40), AbSummary(0.80), shouldPromote = false, reason = "regressed")),
            baselineFactory = { NoopGenerator() },
            candidateFactory = { NoopGenerator() },
            onPromote = { promoted = true },
        )
        val verdict = loop.cycleAsync("s")
        assertFalse(promoted)
        assertTrue(verdict.improvementsApplied.startsWith("rejected"))
        assertEquals(0.0, loop.bestScoreFor("s"), 0.0)
    }

    @Test
    fun `blank suite id falls back to default`() = runTest {
        val loop = SelfBenchSelfImprovementLoop(
            registry = registry("default" to listOf(BenchTask("t", "p"))),
            runner = runner(AbVerdict(AbSummary(0.7), AbSummary(0.5), true, "ok")),
            baselineFactory = { NoopGenerator() },
            candidateFactory = { NoopGenerator() },
        )
        val verdict = loop.cycleAsync("")
        assertTrue(verdict.improvementsApplied.startsWith("promoted"))
    }
}
