// WorldModel.kt
//
// Kotlin port of the CircleAI.Companion world-model contract and its two
// concrete implementations — the C# reference is the exact spec:
//   IWorldModel            (HerJarvisContracts.cs, contract 5)
//   CausalPrediction       (record)
//   FrequencyWorldModel    (HerJarvisRealImplementations.cs, impl 5)
//   BayesianWorldModel     (BayesianWorldModel.cs, Phase E3)
//
// A world model learns P(outcome | observations) from registered evidence and
// predicts the most probable outcome for a scenario. The scenario is a JSON
// object; each property becomes one "name=value" observation token (mirroring
// the C# JsonElement.ToString() extraction). Both models are in-memory,
// deterministic, and thread-safe.
//
// The C# dictionaries use StringComparer.OrdinalIgnoreCase, which matches
// case-insensitively but RETURNS the originally-stored key. We reproduce that
// with CiCounter: keyed by lower-case for lookup, remembering the first-seen
// original spelling so the predicted outcome keeps its original casing.

package com.bhengubv.circleai.companion.reasoning

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.exp
import kotlin.math.ln
import kotlin.math.max

// ---------------------------------------------------------------------------
// CausalPrediction
// ---------------------------------------------------------------------------

/**
 * A predicted [outcome] with its [probability] and the [supportingFactors] that
 * drove it. Mirrors the C# `CausalPrediction` record.
 */
data class CausalPrediction(
    val outcome: String,
    val probability: Double,
    val supportingFactors: List<String>,
)

// ---------------------------------------------------------------------------
// IWorldModel
// ---------------------------------------------------------------------------

/** World model + causal reasoning: predict an outcome for a scenario. */
interface IWorldModel {
    /**
     * Predict the most probable outcome for [scenarioJson] (a JSON object whose
     * properties are the observed variables).
     */
    suspend fun predictAsync(scenarioJson: String): CausalPrediction
}

// ---------------------------------------------------------------------------
// Case-insensitive counter (mirrors StringComparer.OrdinalIgnoreCase)
// ---------------------------------------------------------------------------

/**
 * A thread-safe count-per-key map that matches keys case-insensitively but
 * remembers the first-seen original spelling of each key — exactly how a .NET
 * `Dictionary<string,long>` created with `StringComparer.OrdinalIgnoreCase`
 * behaves. [increment] adds to a key; [snapshot] exposes (originalKey, count).
 */
internal class CiCounter {
    private data class Cell(val original: String, var count: Long)

    private val cells = ConcurrentHashMap<String, Cell>()

    fun increment(key: String, by: Long = 1L) {
        cells.compute(key.lowercase()) { _, existing ->
            if (existing == null) Cell(key, by) else existing.also { it.count += by }
        }
    }

    fun getCount(key: String): Long = cells[key.lowercase()]?.count ?: 0L

    fun total(): Long = cells.values.sumOf { it.count }

    fun size(): Int = cells.size

    val isEmpty: Boolean get() = cells.isEmpty()

    /** (originalKey, count) for every distinct key. */
    fun snapshot(): List<Pair<String, Long>> = cells.values.map { it.original to it.count }
}

// ---------------------------------------------------------------------------
// Shared observation extraction
// ---------------------------------------------------------------------------

private val EXTRACT_JSON: Json = Json { isLenient = true }

/**
 * Turns a scenario JSON object into a flat list of `name=value` observation
 * tokens, byte-for-byte matching the C# `ExtractObservations`
 * (`prop.Name + "=" + prop.Value.ToString()`). The rendering reproduces .NET's
 * `JsonElement.ToString()` exactly, which differs from a naive `toString()`:
 *   - string  → the unquoted content        ("rain")
 *   - number  → the raw JSON number text     ("5", "3.5")
 *   - `true`  → `True`   and `false` → `False`   (capitalised, .NET bool.ToString)
 *   - `null`  → the empty string             ("")
 *   - object/array → minified JSON           ({"a":1}, [1,2])
 *
 * Returns an empty list when [scenarioJson] is blank, not a JSON object, or not
 * parseable — the C# code catches `JsonException` and returns empty.
 */
internal fun extractObservations(scenarioJson: String?): List<String> {
    if (scenarioJson.isNullOrBlank()) return emptyList()
    return try {
        val root = EXTRACT_JSON.parseToJsonElement(scenarioJson)
        if (root !is JsonObject) return emptyList()
        root.entries.map { (name, value) -> "$name=${renderJsonElement(value)}" }
    } catch (_: Exception) {
        emptyList()
    }
}

/** Reproduces .NET `JsonElement.ToString()` for a kotlinx [value]. */
private fun renderJsonElement(value: kotlinx.serialization.json.JsonElement): String = when (value) {
    is JsonPrimitive -> when {
        value is kotlinx.serialization.json.JsonNull -> ""       // .NET: null -> ""
        value.isString -> value.content                          // unquoted string
        value.content == "true" -> "True"                        // .NET: bool.ToString()
        value.content == "false" -> "False"
        else -> value.content                                    // number: raw text
    }
    else -> value.toString()                                     // object/array: minified JSON
}

// ---------------------------------------------------------------------------
// FrequencyWorldModel
// ---------------------------------------------------------------------------

/**
 * Learns P(outcome | observation) as raw frequencies. At predict time it tallies
 * every outcome ever seen alongside any of the scenario's observations and
 * returns the most-frequent one, with probability = topCount / totalTally.
 *
 * Observation and outcome keys are matched case-insensitively (C#
 * `OrdinalIgnoreCase`). When no evidence matches, returns `("unknown", 0.5, …)`.
 */
class FrequencyWorldModel : IWorldModel {
    // observation -> { outcome -> count } (both case-insensitive).
    private val counts = ConcurrentHashMap<String, CiCounter>()

    /** Tell the model: when these observations happen, this outcome was seen. */
    fun observe(observations: Iterable<String>, outcome: String) {
        require(outcome.isNotBlank()) { "outcome required" }
        for (obs in observations) {
            val inner = counts.getOrPut(obs.lowercase()) { CiCounter() }
            inner.increment(outcome)
        }
    }

    override suspend fun predictAsync(scenarioJson: String): CausalPrediction {
        val observations = extractObservations(scenarioJson)
        // tally keyed case-insensitively, preserving the original outcome spelling.
        val tally = CiCounter()
        val supporters = ArrayList<String>()
        for (obs in observations) {
            val inner = counts[obs.lowercase()] ?: continue
            supporters.add(obs)
            for ((outcome, n) in inner.snapshot()) {
                tally.increment(outcome, n)
            }
        }
        if (tally.isEmpty) return CausalPrediction("unknown", 0.5, supporters)
        val total = tally.total()
        val top = tally.snapshot().maxByOrNull { it.second }!!
        return CausalPrediction(top.first, top.second.toDouble() / total, supporters)
    }
}

// ---------------------------------------------------------------------------
// BayesianWorldModel
// ---------------------------------------------------------------------------

/**
 * A small online-learning Naive Bayes classifier over (observations → outcome)
 * pairs. At predict time, for every seen outcome it evaluates
 *   log P(outcome | obs) = log P(outcome) + Σ log P(obs_i | outcome)
 * with Laplace smoothing (strength [laplaceAlpha]), then softmaxes the
 * log-posteriors to a normalised probability for the top outcome.
 *
 * When there are no observations or no evidence yet, returns
 * `("unknown", 0.5, [])`.
 */
class BayesianWorldModel(private val laplaceAlpha: Double = 1.0) : IWorldModel {

    init {
        require(laplaceAlpha > 0) { "laplaceAlpha must be > 0" }
    }

    private val outcomeCounts = CiCounter()
    // outcome (lower-cased key) -> { observation -> count } (case-insensitive).
    private val condCounts = ConcurrentHashMap<String, CiCounter>()
    private val vocab = HashSet<String>()
    private val vocabLock = Any()

    @Volatile
    private var totalObservations: Long = 0
    private val totalLock = Any()

    /** Update the model with one (observations → outcome) example. */
    fun observe(observations: Iterable<String>, outcome: String) {
        require(outcome.isNotBlank()) { "outcome required" }

        outcomeCounts.increment(outcome)
        synchronized(totalLock) { totalObservations++ }

        val cond = condCounts.getOrPut(outcome.lowercase()) { CiCounter() }
        for (obs in observations) {
            if (obs.isBlank()) continue
            cond.increment(obs)
            synchronized(vocabLock) { vocab.add(obs.lowercase()) }
        }
    }

    override suspend fun predictAsync(scenarioJson: String): CausalPrediction {
        val observations = extractObservations(scenarioJson)
        if (observations.isEmpty() || outcomeCounts.isEmpty) {
            return CausalPrediction("unknown", 0.5, emptyList())
        }

        val vocabSize = max(1, synchronized(vocabLock) { vocab.size })
        val totalEx = max(1L, totalObservations)
        val numOutcomes = outcomeCounts.size()

        val scored = ArrayList<Pair<String, Double>>(numOutcomes)
        for ((outcome, outcomeCount) in outcomeCounts.snapshot()) {
            // Log P(outcome) — Laplace-smoothed prior.
            val logPrior = ln((outcomeCount + laplaceAlpha) / (totalEx + laplaceAlpha * numOutcomes))

            val cond = condCounts[outcome.lowercase()]
            val totalForOutcome = cond?.total() ?: 0L
            var logLikelihood = 0.0
            for (obs in observations) {
                val n = cond?.getCount(obs) ?: 0L
                val p = (n + laplaceAlpha) / (totalForOutcome + laplaceAlpha * vocabSize)
                logLikelihood += ln(p)
            }
            scored.add(outcome to (logPrior + logLikelihood))
        }

        // Softmax over log-posteriors for a normalised probability.
        val maxLogPost = scored.maxOf { it.second }
        val expSum = scored.sumOf { exp(it.second - maxLogPost) }
        val top = scored.maxByOrNull { it.second }!!
        val prob = exp(top.second - maxLogPost) / expSum
        return CausalPrediction(top.first, prob, observations)
    }
}
