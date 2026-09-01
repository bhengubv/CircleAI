// CueExtractor.kt
//
// Turning what was said into what is remembered, with NO MODEL.
//
// THE FLOOR THAT ALWAYS WORKS. A model reads a conversation better than a list
// of phrases ever will, and a memory that only fills itself when a model is
// loaded does not fill itself on a phone in aeroplane mode. So this is the
// mechanism, not the degraded mode - the same call the store makes with FTS5
// and LIKE, and the same one the whole design makes about embeddings.
//
// IT KEEPS THE PERSON WORDS. Every atom is a sentence somebody actually said,
// lifted whole. Paraphrasing is where extraction starts inventing, and an
// invented memory is worse than an empty one because it comes back with the
// same confidence as a true one.
//
// IT LISTENS TO THE PERSON, NOT TO THE ASSISTANT. What an assistant said it
// would do is a plan; what the person said is the requirement. Extracting from
// both would let the thing that was corrected file its own version of events
// alongside the correction - which is how a memory ends up agreeing with
// whoever spoke last.
//
// IT DOES NOT INVENT A SUBJECT. A wrong subject key is worse than none: it
// makes an atom findable in the wrong situation and invisible in the right one.

package com.bhengubv.circleai.memory

import java.util.UUID

class CueExtractor : IAtomExtractor {

    override val name: String get() = "cues"

    override fun extract(episode: EpisodicMemoryEntry, subject: String?): List<AtomCandidate> {
        val found = mutableListOf<AtomCandidate>()
        val seen = HashSet<String>()

        // The person turn only. See the header.
        for (sentence in sentences(episode.userText)) {
            if (sentence.length < SHORTEST_WORTH_KEEPING || sentence.length > LONGEST_WORTH_KEEPING) continue

            val lowered = sentence.lowercase()

            // THE MOST SPECIFIC CUE WINS. "i told you" and "you keep" often sit
            // in one sentence, and filing it twice makes one complaint look
            // like a pattern.
            val cue = CUES
                .filter { c ->
                    val at = position(lowered, c.phrase)
                    at >= 0 && (!c.atStart || at == 0)
                }
                .sortedWith(compareByDescending<Cue> { it.confidence }.thenByDescending { it.phrase.length })
                .firstOrNull() ?: continue

            if (!seen.add(normalise(sentence))) continue

            found.add(
                AtomCandidate(
                    MemoryAtom(
                        kind = cue.kind,
                        text = sentence,
                        subject = subject ?: episode.appContext,
                        outcome = when {
                            cue.failed -> DecisionOutcome.FAILED
                            cue.kind == AtomKind.DECISION -> DecisionOutcome.RESOLVED
                            else -> null
                        },
                        sourceEpisode = runCatching { UUID.fromString(episode.id) }.getOrNull(),
                        recordedAtUtc = episode.recordedAtUtc,
                    ),
                    confidence = cue.confidence,
                    cue = cue.phrase.trim(),
                    quote = sentence,
                ),
            )
        }

        return found
    }

    internal data class Cue(
        val phrase: String,
        val kind: AtomKind,
        val confidence: Double,
        val failed: Boolean = false,
        val atStart: Boolean = false,
    )

    companion object {

        /**
         * A sentence this short is a REACTION, not a requirement. "never mind",
         * "stop it", "I want that" carry a cue and no content, and filing them
         * fills the memory with things that match everything and mean nothing.
         */
        const val SHORTEST_WORTH_KEEPING = 20

        /**
         * And one this long is a paragraph that happens to contain the word,
         * not a rule somebody stated. Keeping it would put a page into a recall
         * budget that holds 600 characters.
         */
        const val LONGEST_WORTH_KEEPING = 240

        /**
         * Ordered by how little they leave open to interpretation. "never" at
         * the START of a sentence is a rule and nothing else; "use" could be
         * anything, which is why it scores where it does.
         */
        internal val CUES: List<Cue> = listOf(
            // A rule, stated. The least ambiguous thing a person says - as long
            // as it is the sentence FIRST word.
            Cue("never ", AtomKind.RULING, 0.92, atStart = true),
            Cue("always ", AtomKind.RULING, 0.88, atStart = true),
            Cue("do not ", AtomKind.RULING, 0.88, atStart = true),
            Cue("don" + Char(39) + "t ", AtomKind.RULING, 0.88, atStart = true),
            Cue("must not ", AtomKind.RULING, 0.90, atStart = true),
            Cue("stop ", AtomKind.RULING, 0.82, atStart = true),
            Cue("we only ", AtomKind.RULING, 0.86),
            Cue("we never ", AtomKind.RULING, 0.90),
            Cue("we always ", AtomKind.RULING, 0.88),
            Cue("from now on", AtomKind.RULING, 0.90),

            // THE SAME RULES WITHOUT THE APOSTROPHE, because that is how people
            // type when they are annoyed - which is exactly when they are
            // stating the rule that was just broken.
            Cue("dont ", AtomKind.RULING, 0.88, atStart = true),
            Cue("wont ", AtomKind.RULING, 0.84, atStart = true),
            Cue("we dont ", AtomKind.RULING, 0.88),
            Cue("we wont ", AtomKind.RULING, 0.84),

            // A road tried and found CLOSED. Worth as much as one that worked,
            // and it is the thing recall pushes to the top.
            Cue("did not work", AtomKind.DECISION, 0.88, failed = true),
            Cue("didn" + Char(39) + "t work", AtomKind.DECISION, 0.88, failed = true),
            Cue("didnt work", AtomKind.DECISION, 0.88, failed = true),
            Cue("does not work", AtomKind.DECISION, 0.88, failed = true),
            Cue("doesn" + Char(39) + "t work", AtomKind.DECISION, 0.88, failed = true),
            Cue("doesnt work", AtomKind.DECISION, 0.88, failed = true),
            Cue("never worked", AtomKind.DECISION, 0.86, failed = true),
            Cue("still broken", AtomKind.DECISION, 0.86, failed = true),
            Cue("that broke", AtomKind.DECISION, 0.84, failed = true),
            Cue("it failed", AtomKind.DECISION, 0.84, failed = true),

            // Being told AGAIN. The single highest-value thing in a transcript:
            // whatever follows has already cost somebody twice.
            Cue("i told you", AtomKind.RULING, 0.90),
            Cue("i already told", AtomKind.RULING, 0.90),
            Cue("i said ", AtomKind.RULING, 0.84),
            Cue("you keep ", AtomKind.RULING, 0.86),
            Cue("how many times", AtomKind.RULING, 0.88),

            // How somebody wants to be worked with.
            Cue("i prefer ", AtomKind.PREFERENCE, 0.88),
            Cue("i" + Char(39) + "d rather ", AtomKind.PREFERENCE, 0.86),
            Cue("i would rather ", AtomKind.PREFERENCE, 0.86),
            Cue("i hate ", AtomKind.PREFERENCE, 0.84),
            Cue("i want ", AtomKind.PREFERENCE, 0.78),
            Cue("i like ", AtomKind.PREFERENCE, 0.76),

            // Something settled.
            Cue("let" + Char(39) + "s use ", AtomKind.DECISION, 0.84),
            Cue("lets use ", AtomKind.DECISION, 0.84),
            Cue("we" + Char(39) + "ll use ", AtomKind.DECISION, 0.84),
            Cue("we will use ", AtomKind.DECISION, 0.84),
            Cue("we" + Char(39) + "re going with", AtomKind.DECISION, 0.86),
            Cue("going with ", AtomKind.DECISION, 0.78),
            Cue("use ", AtomKind.DECISION, 0.66),
            Cue("the answer is", AtomKind.DECISION, 0.72),
        )

        /**
         * Where the phrase starts, or -1.
         *
         * It must not be INSIDE a word: "use " matching in "abuse the" would
         * file a decision nobody made. Only a boundary counts.
         */
        internal fun position(haystack: String, needle: String): Int {
            var from = 0
            while (from <= haystack.length - needle.length) {
                val at = haystack.indexOf(needle, from)
                if (at < 0) return -1
                if (at == 0 || !haystack[at - 1].isLetterOrDigit()) return at
                from = at + 1
            }
            return -1
        }

        /**
         * A full stop only ends a sentence when whitespace or the end follows -
         * otherwise every version number and every file name splits a rule in
         * half.
         */
        internal fun sentences(text: String): List<String> {
            if (text.isBlank()) return emptyList()

            val trimChars = charArrayOf(' ', '\t', '-', '*', '>', '.', ',')
            val out = mutableListOf<String>()
            var start = 0
            for (i in text.indices) {
                val c = text[i]
                val ends = c == '\n' || c == '\r' || c == '?' || c == '!' ||
                    (c == '.' && (i + 1 >= text.length || text[i + 1].isWhitespace()))
                if (!ends) continue

                val sentence = text.substring(start, i).trim(*trimChars)
                if (sentence.isNotEmpty()) out.add(sentence)
                start = i + 1
            }

            val last = text.substring(start).trim(*trimChars)
            if (last.isNotEmpty()) out.add(last)
            return out
        }

        /**
         * The key a store uses to answer "do I already know this".
         *
         * Case, spacing and trailing punctuation are all noise: the same
         * sentence typed twice with different punctuation is one memory, and
         * filing it twice is how a memory starts repeating itself.
         */
        fun normalise(text: String): String =
            text.lowercase()
                .split(Char(32), Char(9), Char(10), Char(13))
                .filter { it.isNotEmpty() }
                .joinToString(" ")
                .trim('.', ',', '!', '?', ';', ':', ' ')
    }
}
