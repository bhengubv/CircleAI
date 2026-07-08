// TheoryOfMind.kt
//
// Kotlin port of the CircleAI.Companion theory-of-mind contract and its
// concrete implementation — the C# reference is the exact spec:
//   ITheoryOfMind              (HerJarvisContracts.cs, contract 10)
//   OtherMindEstimate          (record)
//   BeliefTrackerTheoryOfMind  (HerJarvisRealImplementations.cs, impl 10)
//
// Theory of mind = inferring what another party likely believes, from an
// interaction history. BeliefTrackerTheoryOfMind is a bag-of-belief inference:
// it scans the history for "X thinks/believes/wants/fears/hopes …" claims,
// accumulates a decayed, verb-weighted score per claim, and serialises the
// belief map to JSON with a confidence saturating at five points of evidence.

package com.bhengubv.circleai.companion.reasoning

// ---------------------------------------------------------------------------
// OtherMindEstimate
// ---------------------------------------------------------------------------

/**
 * An estimate of another party's mental state: [likelyBeliefJson] is a JSON map
 * of `verb:claim` → accumulated weight, [confidence] saturates at 1.0. Mirrors
 * the C# `OtherMindEstimate` record.
 */
data class OtherMindEstimate(
    val targetIdentifier: String,
    val likelyBeliefJson: String,
    val confidence: Double,
)

// ---------------------------------------------------------------------------
// ITheoryOfMind
// ---------------------------------------------------------------------------

/** Theory of mind: estimate another party's likely beliefs. */
interface ITheoryOfMind {
    suspend fun estimateAsync(target: String, interactionHistoryJson: String): OtherMindEstimate
}

// ---------------------------------------------------------------------------
// BeliefTrackerTheoryOfMind
// ---------------------------------------------------------------------------

/**
 * Bag-of-belief inference with confidence decay. For every "verb claim" match
 * (verb ∈ think(s)/believe(s)/want(s)/fear(s)/hope(s)) in order of appearance:
 *   - decay  = 1 / (1 + idx * 0.1)              (earlier mentions weigh more)
 *   - weight = 1.0 if the verb starts "believ", else 0.7
 *   - key    = "verb:claim"                     (claim trimmed)
 * The per-key scores accumulate; the map is serialised to JSON and the overall
 * confidence is min(1.0, Σscores / 5). Keys are matched case-insensitively
 * (C# `OrdinalIgnoreCase`), keeping the first-seen spelling.
 */
class BeliefTrackerTheoryOfMind : ITheoryOfMind {

    override suspend fun estimateAsync(target: String, interactionHistoryJson: String): OtherMindEstimate {
        require(target.isNotBlank()) { "target required" }
        // A case-insensitive, insertion-ordered accumulator preserving original keys.
        val beliefs = LinkedHashMap<String, Pair<String, Double>>() // lowerKey -> (originalKey, score)
        var idx = 0
        for (m in BELIEF_RX.findAll(interactionHistoryJson)) {
            val verb = m.groupValues[1].lowercase()
            val claim = m.groupValues[2].trim()
            val decay = 1.0 / (1.0 + idx * 0.1)
            val weight = if (verb.startsWith("believ")) 1.0 else 0.7
            val key = "$verb:$claim"
            val lower = key.lowercase()
            val existing = beliefs[lower]
            beliefs[lower] = if (existing == null) {
                key to (weight * decay)
            } else {
                existing.first to (existing.second + weight * decay)
            }
            idx++
        }
        val json = serializeBeliefs(beliefs.values)
        val sum = beliefs.values.sumOf { it.second }
        val conf = if (beliefs.isEmpty()) 0.0 else minOf(1.0, sum / 5.0)
        return OtherMindEstimate(target, json, conf)
    }

    private companion object {
        // \b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)  (case-insensitive)
        val BELIEF_RX = Regex(
            "\\b(thinks?|believes?|wants?|fears?|hopes?)\\s+([^.;!?]+)",
            RegexOption.IGNORE_CASE,
        )

        /**
         * Serialises the (originalKey, score) pairs to a JSON object matching
         * .NET `JsonSerializer.Serialize(Dictionary<string,double>)`: insertion
         * order, JSON-escaped keys, and doubles in shortest round-trip form with
         * whole numbers rendered without a trailing ".0".
         */
        fun serializeBeliefs(entries: Collection<Pair<String, Double>>): String {
            val sb = StringBuilder()
            sb.append('{')
            var first = true
            for ((key, value) in entries) {
                if (!first) sb.append(',')
                first = false
                sb.append(escapeJson(key)).append(':').append(formatDouble(value))
            }
            sb.append('}')
            return sb.toString()
        }

        /** .NET-compatible double rendering: strip a trailing ".0" from whole numbers. */
        fun formatDouble(v: Double): String {
            val s = v.toString()
            return if (s.endsWith(".0")) s.substring(0, s.length - 2) else s
        }

        /** JSON-escape a string, wrapping it in double quotes. */
        fun escapeJson(s: String): String {
            val sb = StringBuilder(s.length + 2)
            sb.append('"')
            for (c in s) {
                when {
                    c == '"' -> sb.append("\\\"")
                    c == '\\' -> sb.append("\\\\")
                    c == '\n' -> sb.append("\\n")
                    c == '\r' -> sb.append("\\r")
                    c == '\t' -> sb.append("\\t")
                    c.code < 0x20 -> sb.append("\\u%04x".format(c.code))
                    else -> sb.append(c)
                }
            }
            sb.append('"')
            return sb.toString()
        }
    }
}
