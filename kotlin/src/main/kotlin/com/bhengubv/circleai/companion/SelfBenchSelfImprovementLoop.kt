// SelfBenchSelfImprovementLoop.kt
//
// Kotlin port of CircleAI.Companion.SelfBenchSelfImprovementLoop (Phase E7) —
// the C# reference (SelfBenchSelfImprovementLoop.cs) is the EXACT spec.
// Implements the HER/Jarvis ISelfImprovementLoop by orchestrating CircleAI.SelfBench:
// run the named suite against the current AIService as baseline, ask the host for
// a candidate AIService, A/B compare, and only "apply" (promote) the candidate if
// the regression gate passes. The promote step is a host-supplied callback so this
// class stays free of adapter-management plumbing — it just runs the gate.
//
// The SelfBench types (BenchSuiteRegistry / AbBenchRunner / AbVerdict /
// RegressionGateConfig) do not exist in the Kotlin tree yet, so — per the porting
// rules — the minimal SelfBench contracts this loop consumes are declared here and
// injected. The existing inference IChatGenerator stands in for IAIService.

package com.bhengubv.circleai.companion

import com.bhengubv.circleai.companion.herjarvis.ISelfImprovementLoop
import com.bhengubv.circleai.companion.herjarvis.SelfImprovementVerdict
import com.bhengubv.circleai.inference.IChatGenerator
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.max

// ---------------------------------------------------------------------------
// Minimal SelfBench contracts (ports of the CircleAI.SelfBench shapes used)
// ---------------------------------------------------------------------------

/** One benchmark task in a suite. Opaque prompt/expectation the runner scores. */
data class BenchTask(val id: String, val prompt: String, val expectation: String = "")

/** Registry of bench suites keyed by id. Mirrors `BenchSuiteRegistry.Get(id)`. */
interface BenchSuiteRegistry {
    fun get(suiteId: String): List<BenchTask>
}

/** Aggregate scoring for one side of an A/B run. Mirrors `AbSummary` (MeanScore used). */
data class AbSummary(val meanScore: Double, val taskCount: Int = 0)

/** The A/B comparison verdict. Mirrors the fields of C# `AbVerdict` used by the loop. */
data class AbVerdict(
    val candidateSummary: AbSummary,
    val baselineSummary: AbSummary,
    val shouldPromote: Boolean,
    val reason: String,
)

/** Regression-gate tuning. Mirrors `RegressionGateConfig` (defaults preserved). */
data class RegressionGateConfig(
    /** Candidate must beat baseline by at least this margin to promote. Default 0.0. */
    val minImprovement: Double = 0.0,
    /** Candidate may not drop below baseline by more than this. Default 0.0 (no regression). */
    val maxRegression: Double = 0.0,
)

/** Runs an A/B benchmark comparison of a candidate vs a baseline generator. */
interface AbBenchRunner {
    suspend fun compareAsync(
        suiteId: String,
        tasks: List<BenchTask>,
        baseline: IChatGenerator,
        candidate: IChatGenerator,
        gate: RegressionGateConfig,
    ): AbVerdict
}

// ---------------------------------------------------------------------------
// SelfBenchSelfImprovementLoop
// ---------------------------------------------------------------------------

/**
 * A/B self-improvement over a SelfBench suite. Each cycle resolves a baseline
 * and candidate generator, compares them through [runner], and promotes the
 * candidate (invoking [onPromote]) only when the verdict says so.
 */
class SelfBenchSelfImprovementLoop(
    private val registry: BenchSuiteRegistry,
    private val runner: AbBenchRunner,
    private val baselineFactory: suspend () -> IChatGenerator,
    private val candidateFactory: suspend () -> IChatGenerator,
    private val onPromote: (suspend (AbVerdict) -> Unit)? = null,
    private val gate: RegressionGateConfig = RegressionGateConfig(),
) : ISelfImprovementLoop {

    private val bestScores = ConcurrentHashMap<String, Double>()

    override suspend fun cycleAsync(benchSuiteId: String): SelfImprovementVerdict {
        val suiteId = benchSuiteId.ifBlank { "default" }
        val tasks = registry.get(suiteId)
        if (tasks.isEmpty()) {
            return SelfImprovementVerdict("skipped: no tasks in suite", 0.0)
        }

        val baseline = baselineFactory()
        val candidate = candidateFactory()

        val verdict = runner.compareAsync(suiteId, tasks, baseline, candidate, gate)

        val newScore = verdict.candidateSummary.meanScore
        val applied: String
        if (verdict.shouldPromote) {
            onPromote?.invoke(verdict)
            bestScores.compute(suiteId) { _, prev -> if (prev == null) newScore else max(prev, newScore) }
            applied = "promoted candidate (${verdict.reason})"
        } else {
            applied = "rejected (${verdict.reason})"
        }
        return SelfImprovementVerdict(applied, newScore)
    }

    fun bestScoreFor(suiteId: String): Double = bestScores[suiteId] ?: 0.0
}
