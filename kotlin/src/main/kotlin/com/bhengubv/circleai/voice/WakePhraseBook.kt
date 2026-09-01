// WakePhraseBook.kt
//
// Judging a wake phrase BEFORE somebody relies on it: can the listener even
// represent it, is it long enough to be heard across a room, and does it
// collide with one already in the book.
//
// Port of CircleAI.Voice/WakePhraseBook.cs.

package com.bhengubv.circleai.voice

enum class WakePhraseVerdict { GOOD, CAUTION, UNUSABLE }

data class WakePhrase(
    val text: String,
    val tokens: List<String>,
    val verdict: WakePhraseVerdict,
    /** Said to a PERSON, not logged - this is what appears under the field. */
    val advice: String,
    val threshold: Double? = null,
    val boost: Double? = null,
)

class WakePhraseBook(private val tokenizer: SentencePieceTokenizer) {

    private val stored = mutableListOf<WakePhrase>()

    val phrases: List<WakePhrase> get() = stored.toList()

    /**
     * The best usable candidate for a language: the LONGEST one, because more
     * tokens means fewer false wakes.
     */
    fun best(languageCode: String?): WakePhrase? {
        var best: WakePhrase? = null
        for (candidate in candidates(languageCode)) {
            val judged = evaluate(candidate)
            if (judged.verdict == WakePhraseVerdict.UNUSABLE) continue
            if (best == null || judged.tokens.size > best.tokens.size) best = judged
        }
        return best
    }

    fun evaluate(text: String, threshold: Double? = null, boost: Double? = null): WakePhrase {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) {
            return WakePhrase(
                trimmed, emptyList(), WakePhraseVerdict.UNUSABLE,
                "Type something to say.", threshold, boost,
            )
        }

        val tokens = tokenizer.encode(trimmed)

        val (ok, unknown) = tokenizer.canRepresent(trimmed)
        if (!ok) {
            return WakePhrase(
                trimmed, tokens, WakePhraseVerdict.UNUSABLE,
                "This wake word uses sounds the listener does not know (" +
                    unknown.joinToString(", ") + "). Try a different word.",
                threshold, boost,
            )
        }

        // A PREFIX COLLISION makes one of the two phrases dead: the shorter one
        // always fires first, so the longer can never complete. Said in terms of
        // what will happen to the person, not in terms of a trie.
        for (other in stored) {
            if (startsWith(tokens, other.tokens)) {
                return WakePhrase(
                    trimmed, tokens, WakePhraseVerdict.UNUSABLE,
                    other.text + " would always trigger first, so this one could never work. " +
                        "Remove that one, or pick something that does not start the same way.",
                    threshold, boost,
                )
            }
            if (startsWith(other.tokens, tokens)) {
                return WakePhrase(
                    trimmed, tokens, WakePhraseVerdict.UNUSABLE,
                    "This would always trigger before " + other.text + ", which would stop working.",
                    threshold, boost,
                )
            }
        }

        if (tokens.size < MIN_RELIABLE_TOKENS) {
            return WakePhrase(
                trimmed, tokens, WakePhraseVerdict.CAUTION,
                "This is very short, so it may not be heard from across a room. " +
                    "A slightly longer phrase is more reliable.",
                threshold, boost,
            )
        }

        val words = trimmed.split(Char(32)).filter { it.isNotEmpty() }
            .map { it.trim(',', '.', '!', '?').lowercase() }
        if (words.isNotEmpty() && words.all { EVERYDAY.contains(it) }) {
            return WakePhrase(
                trimmed, tokens, WakePhraseVerdict.CAUTION,
                "These are everyday words, so it may wake up when you are talking to someone else.",
                threshold, boost,
            )
        }

        return WakePhrase(trimmed, tokens, WakePhraseVerdict.GOOD, "", threshold, boost)
    }

    /**
     * Adds when usable. An UNUSABLE phrase is not stored, so the book can never
     * hold one that cannot fire.
     */
    fun tryAdd(text: String, threshold: Double? = null, boost: Double? = null): Pair<Boolean, WakePhrase> {
        val phrase = evaluate(text, threshold, boost)
        if (phrase.verdict == WakePhraseVerdict.UNUSABLE) return false to phrase
        stored.add(phrase)
        return true to phrase
    }

    fun remove(text: String): Boolean {
        val before = stored.size
        stored.removeAll { it.text.equals(text, ignoreCase = true) }
        return stored.size != before
    }

    companion object {
        /**
         * Below this, a phrase is too short to be heard reliably from across a
         * room. NOT a hard refusal - a caution, because the user may still want it.
         */
        const val MIN_RELIABLE_TOKENS = 4

        /**
         * Words common enough that a wake phrase built ONLY from them will fire
         * while somebody is talking to another person.
         */
        internal val EVERYDAY: Set<String> = setOf(
            "circle", "listen", "hello", "hey", "okay", "ok", "yes", "no", "stop", "go",
            "play", "open", "close", "help", "please", "wait", "back", "up", "down",
            "phone", "call", "text", "time", "now", "today", "one", "two", "three",
        )

        val candidatesByLanguage: Map<String, List<String>> = mapOf(
            "en" to listOf("Hey B"),
            "ja" to listOf("ビーさん", "ビーさま", "Bee san"),
            "ko" to listOf("비 님", "Bee nim"),
            "zh" to listOf("小B", "Xiao B"),
            "yue" to listOf("小B", "Siu B"),
        )

        /**
         * en-ZA and en both find the English list: the region is dropped, and an
         * UNKNOWN language falls back to English rather than to nothing.
         */
        fun candidates(languageCode: String?): List<String> {
            var code = (languageCode ?: "").trim()
            val cut = code.indexOf('-')
            if (cut > 0) code = code.substring(0, cut)
            return candidatesByLanguage[code.lowercase()] ?: candidatesByLanguage["en"]!!
        }

        /** A prefix must be SHORTER than what it prefixes - a phrase does not collide with itself. */
        internal fun startsWith(longer: List<String>, prefix: List<String>): Boolean {
            if (prefix.isEmpty() || prefix.size >= longer.size) return false
            for (i in prefix.indices) if (longer[i] != prefix[i]) return false
            return true
        }
    }
}
