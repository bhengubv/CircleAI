// AtomLearner.kt
//
// What gets KEPT, out of what was spotted.
//
// THIS IS THE HALF THAT DECIDES, and it is separate from the half that spots so
// that "what did you see" and "what did you keep" are two questions with two
// answers. An extractor that also committed would make a wrong reading
// unfalsifiable: there would be nothing to look at but the atom it produced.
//
// TWICE MUST NOT MEAN TWO. Running this over the same conversation again -
// after a crash, a pull, or simply a second pass - has to be the same as
// running it once. Everything below follows from that, and it is why a
// near-duplicate is DROPPED rather than counted.
//
// IT NEVER SUPERSEDES ON A GUESS. Correcting is how an atom history is
// rewritten and how its rank climbs; doing that because two sentences looked
// similar would let a misreading quietly replace something a person actually
// said. Only a person, or an explicit call, supersedes.

package com.bhengubv.circleai.memory

data class LearnReport(
    val considered: Int,
    val recorded: List<AtomCandidate>,
    val alreadyKnown: List<AtomCandidate>,
    val offered: List<AtomCandidate>,
)

class AtomLearner(private val extractor: IAtomExtractor = CueExtractor()) {

    val extractorName: String get() = extractor.name

    /** The convenience form: a snapshot of what is already remembered. */
    suspend fun learn(
        episodes: Iterable<EpisodicMemoryEntry>,
        record: suspend (MemoryAtom) -> Unit,
        known: List<MemoryAtom>,
        subject: String? = null,
    ): LearnReport {
        val already = known.map { CueExtractor.normalise(it.text) }.toHashSet()
        return learn(episodes, record, { text -> already.contains(CueExtractor.normalise(text)) }, subject)
    }

    suspend fun learn(
        episodes: Iterable<EpisodicMemoryEntry>,
        record: suspend (MemoryAtom) -> Unit,
        knows: suspend (String) -> Boolean,
        subject: String? = null,
    ): LearnReport {
        // Still a SET as well, because two identical sentences in ONE pass are
        // not yet in any store and would otherwise both be kept.
        val seen = HashSet<String>()
        var considered = 0
        val recorded = mutableListOf<AtomCandidate>()
        val alreadyKnown = mutableListOf<AtomCandidate>()
        val offered = mutableListOf<AtomCandidate>()

        // OLDEST FIRST, so that when two passes over the same conversation
        // produce the same sentence twice, the one kept is the one said first -
        // and a rebuild lands on the same atom either way.
        for (episode in episodes.sortedBy { it.recordedAtUtc }) {
            for (candidate in extractor.extract(episode, subject)) {
                considered++

                // ALREADY KNOWN BEATS NOT SURE ENOUGH. A sentence that is
                // already remembered is not a question for anybody, however
                // faintly it was spotted, and offering it would ask somebody to
                // confirm what they already told us.
                val normalised = CueExtractor.normalise(candidate.atom.text)
                if (!seen.add(normalised) || knows(candidate.atom.text)) {
                    alreadyKnown.add(candidate)
                    continue
                }

                if (!candidate.certain) {
                    offered.add(candidate)
                    continue
                }

                record(candidate.atom)
                recorded.add(candidate)
            }
        }

        return LearnReport(considered, recorded, alreadyKnown, offered)
    }

    /** What was spotted, without keeping any of it. */
    fun read(episode: EpisodicMemoryEntry, subject: String? = null): List<AtomCandidate> =
        extractor.extract(episode, subject)
}
