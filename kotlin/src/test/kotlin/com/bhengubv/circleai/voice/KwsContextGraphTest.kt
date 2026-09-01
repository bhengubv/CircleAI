package com.bhengubv.circleai.voice

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class KwsContextGraphTest {

    /** Walks a token sequence from the root and returns everything it completed. */
    private fun walk(g: KwsContextGraph, tokens: List<Int>): List<String> {
        var state = g.root
        val hits = mutableListOf<String>()
        for (t in tokens) {
            val step = g.forwardOneStep(state, t)
            state = step.state
            step.matched?.let { hits.add(it.phrase) }
        }
        return hits
    }

    private fun graph(
        vararg phrases: Pair<String, List<Int>>,
        contextScore: Float = 1f,
        acThreshold: Float = 0.25f,
        scores: List<Float>? = null,
    ) = KwsContextGraph(
        tokenIds = phrases.map { it.second },
        contextScore = contextScore,
        acThreshold = acThreshold,
        scores = scores,
        phrases = phrases.map { it.first },
    )

    @Test
    fun aSinglePhraseIsFoundWhenItsTokensArrive() {
        val g = graph("hey b" to listOf(1, 2, 3))
        assertEquals(listOf("hey b"), walk(g, listOf(1, 2, 3)))
    }

    @Test
    fun aPhraseIsNotReportedUntilItsLastToken() {
        val g = graph("hey b" to listOf(1, 2, 3))
        assertTrue(walk(g, listOf(1, 2)).isEmpty())
    }

    @Test
    fun severalPhrasesAreWatchedForInONEpass() {
        // The reason the structure is a trie rather than a list of matchers.
        val g = graph(
            "hey b" to listOf(1, 2, 3),
            "circle" to listOf(4, 5),
            "wake up" to listOf(6, 7, 8),
        )
        assertEquals(listOf("circle"), walk(g, listOf(4, 5)))
        assertEquals(listOf("wake up"), walk(g, listOf(6, 7, 8)))
    }

    @Test
    fun aPhraseIsFoundAfterAFalseStartOnAnother() {
        // The FAIL LINK. Tokens 1,2 start "hey b" and then 4,5 arrive; without
        // a fail link the walk is stuck partway into the first phrase and the
        // second never fires.
        val g = graph("hey b" to listOf(1, 2, 3), "circle" to listOf(4, 5))
        assertEquals(listOf("circle"), walk(g, listOf(1, 2, 4, 5)))
    }

    @Test
    fun aPartialMatchFallsBackIntoTheRIGHTsharedPrefix() {
        // 1,2,1,2,3 - the third token restarts the phrase rather than being
        // discarded, so the phrase still completes.
        val g = graph("hey b" to listOf(1, 2, 3))
        assertEquals(listOf("hey b"), walk(g, listOf(1, 2, 1, 2, 3)))
    }

    @Test
    fun aShorterPhraseFinishingINSIDEaLongerOneIsNotSwallowed() {
        // The OUTPUT LINK. "b" ends inside "hey b"; without output links the
        // longer walk reports only itself and the shorter phrase never fires.
        val g = graph("she" to listOf(1, 2, 3), "he" to listOf(2, 3))
        val hits = walk(g, listOf(1, 2, 3))
        assertTrue(hits.contains("she") || hits.contains("he"), "nothing matched at all")
        assertNotNull(walk(g, listOf(2, 3)).firstOrNull())
    }

    @Test
    fun aPhraseWhoseOWNprefixIsACompletePhraseIsReportedAsSHADOWED() {
        // It can never fire: the shorter one matches first and the walk never
        // reaches the longer end. Reporting it is the difference between a wake
        // word that does not work and a wake word nobody knows does not work.
        val g = graph("hey" to listOf(1, 2), "hey there" to listOf(1, 2, 3, 4))
        assertEquals(1, g.shadowedPhrases.size)
        assertEquals("hey there", g.shadowedPhrases[0].first)
        assertEquals("hey", g.shadowedPhrases[0].second)
    }

    @Test
    fun independentPhrasesShadowNothingAndTheListIsEmpty() {
        val g = graph("hey b" to listOf(1, 2, 3), "circle" to listOf(4, 5))
        assertTrue(g.shadowedPhrases.isEmpty(), "healthy configuration reported a shadow")
    }

    @Test
    fun aSharedPrefixKeepsTheHIGHERboost() {
        // Otherwise one phrase quietly weakens another that starts the same way,
        // and the symptom is a wake word that got less reliable when an
        // unrelated one was added.
        val g = KwsContextGraph(
            tokenIds = listOf(listOf(1, 2), listOf(1, 3)),
            contextScore = 1f,
            acThreshold = 0.25f,
            scores = listOf(2f, 5f),
            phrases = listOf("low", "high"),
        )
        val shared = g.root.next[1]!!
        assertEquals(5f, shared.tokenScore)
    }

    @Test
    fun aZeroScoreOrThresholdFallsBackToTheGraphDEFAULT() {
        // Zero means "not set", not "score this phrase at nothing".
        val g = KwsContextGraph(
            tokenIds = listOf(listOf(1, 2)),
            contextScore = 3f,
            acThreshold = 0.4f,
            scores = listOf(0f),
            phrases = listOf("p"),
            acThresholds = listOf(0f),
        )
        val first = g.root.next[1]!!
        assertEquals(3f, first.tokenScore)
        assertEquals(0.4f, first.next[2]!!.acThreshold)
    }

    @Test
    fun anExplicitPerPhraseThresholdOverridesTheDefault() {
        val g = KwsContextGraph(
            tokenIds = listOf(listOf(1, 2)),
            contextScore = 1f,
            acThreshold = 0.25f,
            phrases = listOf("p"),
            acThresholds = listOf(0.8f),
        )
        assertEquals(0.8f, g.root.next[1]!!.next[2]!!.acThreshold)
    }

    @Test
    fun fallingBackDoesNotREAWARDtheSharedPrefixAlreadyCounted() {
        // The score of a fallback step is the DIFFERENCE between the node
        // scores. Awarding the full node score again inflates a phrase that
        // shares a prefix with a false start, and it will fire on noise.
        val g = KwsContextGraph(
            tokenIds = listOf(listOf(1, 2, 3)),
            contextScore = 1f,
            acThreshold = 0.25f,
            phrases = listOf("p"),
        )
        // Walk 1,2 then 1 again: the fallback lands back at depth 1, and the
        // score for that step must not be another full point of prefix.
        var state = g.root
        var total = 0f
        for (t in listOf(1, 2, 1)) {
            val step = g.forwardOneStep(state, t)
            total += step.score
            state = step.state
        }
        assertEquals(1, state.level, "did not fall back to the right depth")
        assertEquals(1f, total, "the shared prefix was awarded twice")
    }

    @Test
    fun anUnknownTokenAtTheRootStaysAtTheRoot() {
        val g = graph("hey b" to listOf(1, 2, 3))
        val step = g.forwardOneStep(g.root, 99)
        assertEquals(g.root, step.state)
        assertNull(step.matched)
    }

    @Test
    fun theLevelCountsHowFarIntoThePhraseTheWalkHasGot() {
        val g = graph("hey b" to listOf(1, 2, 3))
        var state = g.root
        assertEquals(0, state.level)
        for ((i, t) in listOf(1, 2, 3).withIndex()) {
            state = g.forwardOneStep(state, t).state
            assertEquals(i + 1, state.level)
        }
        assertTrue(state.isEnd)
        assertEquals("hey b", state.phrase)
    }

    @Test
    fun isMatchedAgreesWithWhatTheWalkReported() {
        val g = graph("hey b" to listOf(1, 2, 3))
        var state = g.root
        assertFalse(g.isMatched(state).first)
        for (t in listOf(1, 2)) {
            state = g.forwardOneStep(state, t).state
            assertFalse(g.isMatched(state).first)
        }
        state = g.forwardOneStep(state, 3).state
        val (matched, at) = g.isMatched(state)
        assertTrue(matched)
        assertEquals("hey b", at!!.phrase)
    }

    @Test
    fun anEmptyGraphMatchesNothingAndDoesNotLoopForever() {
        val g = KwsContextGraph(emptyList(), 1f, 0.25f)
        var state = g.root
        for (t in listOf(1, 2, 3, 4, 5)) state = g.forwardOneStep(state, t).state
        assertEquals(g.root, state)
        assertTrue(g.shadowedPhrases.isEmpty())
    }

    @Test
    fun thePrefixPhraseNamesWhichPhraseAPartialWalkIsInside() {
        // What the progress event reads to say "you are 2 of 3 tokens into
        // Hey B", which is how a threshold gets set from evidence.
        val g = graph("hey b" to listOf(1, 2, 3))
        val first = g.root.next[1]!!
        assertEquals("hey b", first.prefixPhrase)
        assertEquals(3, first.prefixLength)
    }
}

class KwsProgressAndKeywordTest {

    @Test
    fun aKeywordDefaultsToNoBoostAndNoOwnThreshold() {
        val k = KwsKeyword(listOf(1, 2, 3), "hey b")
        assertEquals(0f, k.boost)
        assertEquals(0f, k.threshold)
        assertEquals("hey b", k.phrase)
    }

    @Test
    fun progressReportsHowFarInAndAtWhatScore() {
        // The point of surfacing it: the threshold can then be placed between
        // the hits and the misses on real speech rather than at whatever number
        // the upstream project happened to pick.
        val p = KwsProgress("hey b", matched = 2, total = 3, meanProbability = 0.62)
        assertEquals(2, p.matched)
        assertEquals(3, p.total)
        assertEquals(0.62, p.meanProbability)
    }
}

class NullAudioPlayerTest {

    @Test
    fun itSwallowsTheAudioWithoutComplaint() = kotlinx.coroutines.test.runTest {
        val p = NullAudioPlayer.instance
        p.play(ByteArray(1024), 16_000, 1, 16)
        p.play(ByteArray(0), 0, 0, 0)
        p.close()
    }
}
