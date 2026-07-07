// Belief.kt
//
// Memory integrity: attribution + belief revision. Kotlin port of
// Circle.AI.Companion (PersonalBelief, HeuristicBeliefExtractor, SelfBeliefStore)
// — the C# reference — mirroring the TypeScript pilot (companion/belief.ts) and
// Go port (companion_belief.go) 1:1.
//
// Every belief carries WHOSE fact it is — the user's own (Self), someone else's
// (Other), or a general fact (World). The highest-harm rule in the whole system:
// a fact about a third party ("my mother is diabetic") must never be recorded as
// a fact about the user. Only Self beliefs become user facts; a newer self-belief
// on the same predicate supersedes the older one; a correction retracts a belief.

package com.bhengubv.circleai.companion.brain

import java.time.Instant

/** Whose fact a belief is about. */
enum class Attribution { Self, Other, World }

/** A single attributed belief, with provenance and confidence. */
data class PersonalBelief(
    val attribution: Attribution,
    val subject: String,
    val predicate: String,
    val obj: String,
    val confidence: Float,
    val source: String?,
    val recordedAtUtc: Instant,
)

/** Turns a sentence into attributed beliefs. */
interface IBeliefExtractor {
    suspend fun extractAsync(text: String, source: String?): List<PersonalBelief>
}

/**
 * Model-free belief extractor with attribution discipline. Coarse by design — the
 * model-based extractor is far more precise — but it never collapses "my mother"
 * into "me". Attribution is decided by the sentence's leading subject.
 */
class HeuristicBeliefExtractor : IBeliefExtractor {

    override suspend fun extractAsync(text: String, source: String?): List<PersonalBelief> {
        if (text.isBlank()) return emptyList()

        // Split set has NO apostrophe — "i'm" stays one token (matches C# reference).
        val tokens = text.lowercase()
            .split(*SEPARATORS)
            .filter { it.isNotEmpty() }
        if (tokens.isEmpty()) return emptyList()

        val attribution: Attribution
        val subject: String
        val skip = HashSet<Int>() // subject tokens, excluded from the object

        if (tokens.size >= 2 && POSSESSIVE.contains(tokens[0]) && RELATIONS.contains(tokens[1])) {
            // "my mother ..." → someone else
            attribution = Attribution.Other
            subject = tokens[1]
            skip.add(0)
            skip.add(1)
        } else if (RELATIONS.contains(tokens[0])) {
            attribution = Attribution.Other
            subject = tokens[0]
            skip.add(0)
        } else if (tokens[0] == "i" || tokens[0] == "i'm" || tokens[0] == "im" ||
            tokens[0] == "me" || tokens[0] == "my"
        ) {
            // "I ..." or "my <non-relation> ..." → the user
            attribution = Attribution.Self
            subject = "user"
            skip.add(0)
        } else {
            attribution = Attribution.World
            subject = tokens[0]
        }

        val obj = tokens.filterIndexed { i, t ->
            !skip.contains(i) && t.length >= 3 && !STOP.contains(t) && !RELATIONS.contains(t)
        }.joinToString(" ")
        if (obj.isBlank()) return emptyList()

        return listOf(
            PersonalBelief(attribution, subject, "isAbout", obj, 0.6f, source, Instant.now()),
        )
    }

    private companion object {
        val RELATIONS: Set<String> = hashSetOf(
            "mother", "father", "mom", "mum", "dad", "sister", "brother", "wife", "husband", "son", "daughter",
            "aunt", "uncle", "grandmother", "grandfather", "granny", "grandpa", "gran", "nan", "friend",
            "colleague", "boss", "neighbour", "neighbor", "cousin", "partner", "girlfriend", "boyfriend",
        )
        val POSSESSIVE: Set<String> = hashSetOf("my", "her", "his", "their", "our")
        val STOP: Set<String> = hashSetOf(
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "am", "to", "of", "in", "on", "at", "and", "or",
            "but", "with", "has", "have", "had", "that", "this", "it", "as", "for", "really", "very", "just", "now",
        )
        val SEPARATORS = charArrayOf(
            ' ', '\t', '\n', '\r', '.', ',', '?', '!', ';', ':', '"', '(', ')',
        )
    }
}

/**
 * The user's own facts, with attribution filtering, revision, and correction.
 *
 * Thread-safe: the encoder writes from its background drain while the session
 * reads facts for the prompt, so a monitor guards the two lists (matching the C#
 * reference's lock and the Go port's mutex).
 */
class SelfBeliefStore {
    private val gate = Any()
    private val self = ArrayList<PersonalBelief>()
    private val audit = ArrayList<PersonalBelief>() // other/world — remembered, never a user fact

    /** Record a belief. Only Self beliefs become user facts; the rest are audited. */
    fun record(belief: PersonalBelief) {
        synchronized(gate) {
            if (belief.attribution != Attribution.Self) {
                audit.add(belief)
                return
            }
            // Supersede an existing self-belief on the same (subject, predicate): a
            // functional fact holds one current value. The prior value drops out.
            self.removeAll {
                it.subject.equals(belief.subject, ignoreCase = true) &&
                    it.predicate.equals(belief.predicate, ignoreCase = true)
            }
            self.add(belief)
        }
    }

    /** The user's own current facts. */
    fun selfFacts(): List<PersonalBelief> = synchronized(gate) { self.toList() }

    /** Beliefs remembered but never treated as user facts (audit trail). */
    fun nonSelf(): List<PersonalBelief> = synchronized(gate) { audit.toList() }

    /** Correction ("no, that's my mother"): drop any user fact mentioning the text. */
    fun retract(objectContains: String): Int {
        if (objectContains.isBlank()) return 0
        val needle = objectContains.lowercase()
        return synchronized(gate) {
            val before = self.size
            self.removeAll { it.obj.lowercase().contains(needle) }
            before - self.size
        }
    }

    /** Introspection ("why do you think that?"): the source turns behind the user's facts. */
    fun provenance(): List<String> = synchronized(gate) {
        val seen = LinkedHashSet<String>()
        for (b in self) {
            val src = b.source
            if (src != null) seen.add(src)
        }
        seen.toList()
    }
}
