package com.bhengubv.circleai.memory

import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

/** A store that hands back exactly what a test puts in it, in that order. */
private class FakeAtomStore(private val atoms: MutableList<MemoryAtom> = mutableListOf()) : IAtomStore {
    var lastMatchLimit = -1

    override suspend fun add(atom: MemoryAtom) { atoms.add(atom) }
    override suspend fun supersede(oldAtomId: UUID, replacement: MemoryAtom) = replacement
    override suspend fun match(situation: Situation, limit: Int): List<MemoryAtom> {
        lastMatchLimit = limit
        return atoms.filter { it.kind != AtomKind.RELATIONSHIP }.take(limit)
    }
    override suspend fun byKind(kind: AtomKind, limit: Int): List<MemoryAtom> =
        atoms.filter { it.kind == kind }.take(limit)
    override suspend fun all(includeSuperseded: Boolean, limit: Int) = atoms.take(limit)
    override suspend fun knows(text: String) = atoms.any { it.text == text }
    override suspend fun get(id: UUID) = atoms.firstOrNull { it.id == id }
    override suspend fun markVerified(id: UUID, ok: Boolean, whenUtc: Instant) {}
    override suspend fun count() = atoms.size
}

class RecallTest {

    private val now: Instant = Instant.now()

    private fun atom(
        text: String,
        kind: AtomKind = AtomKind.DECISION,
        subject: String? = null,
        corrections: Int = 0,
        outcome: DecisionOutcome? = null,
        verifiedOk: Boolean? = null,
        recorded: Instant = now,
    ) = MemoryAtom(
        kind = kind, text = text, subject = subject, corrections = corrections,
        outcome = outcome, verifiedOk = verifiedOk, recordedAtUtc = recorded,
    )

    private val situation = Situation(verb = "deploy", target = "android/p30")

    @Test
    fun anEmptySituationRecallsNothingWithoutTouchingTheStore() = runTest {
        val store = FakeAtomStore()
        assertEquals(RecallResult.empty, Recall(store).forSituation(Situation()))
        assertEquals(-1, store.lastMatchLimit, "the store was queried for an empty situation")
    }

    @Test
    fun itAsksForMORECandidatesThanTheBudgetSoRankingHasAChoice() = runTest {
        // The store ordering is by subject match, not by what matters here. Ask
        // for exactly the budget and the ranking is decorative.
        val store = FakeAtomStore(mutableListOf(atom("a")))
        Recall(store).forSituation(situation, RecallBudget(maxAtoms = 5))
        assertTrue(store.lastMatchLimit >= 20, "asked for only " + store.lastMatchLimit)
    }

    @Test
    fun aRulingOutranksAPreferenceWhenBothMatch() = runTest {
        val store = FakeAtomStore(
            mutableListOf(
                atom("I like the shorter form of that command", AtomKind.PREFERENCE),
                atom("Never use adb push to install", AtomKind.RULING),
            ),
        )
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 2))
        assertEquals(AtomKind.RULING, out.atoms.first().kind)
    }

    @Test
    fun aRoadAlreadyFOUNDCLOSEDgoesNearTheTop() = runTest {
        // Knowing what failed is worth as much as knowing what worked, and it
        // arrives too late by default: the whole cost of a repeated mistake is
        // paid before anybody remembers making it the first time.
        val store = FakeAtomStore(
            mutableListOf(
                atom("We used the incremental install", AtomKind.DECISION, outcome = DecisionOutcome.RESOLVED),
                atom("The incremental install did not work", AtomKind.DECISION, outcome = DecisionOutcome.FAILED),
            ),
        )
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 2))
        assertTrue(out.atoms.first().failed, "the failure was not surfaced first")
    }

    @Test
    fun aREPEATEDLYCORRECTEDatomOutranksAFreshOne() = runTest {
        val store = FakeAtomStore(
            mutableListOf(
                atom("Something said once", AtomKind.DECISION),
                atom("Something said four times", AtomKind.DECISION, corrections = 4),
            ),
        )
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 2))
        assertEquals("Something said four times", out.atoms.first().text)
    }

    @Test
    fun theCorrectionBonusIsCAPPEDsoOneAtomCannotOwnTheBudgetForever() = runTest {
        val r = Recall(FakeAtomStore())
        val four = r.score(atom("x", corrections = 4), situation, now)
        val forty = r.score(atom("x", corrections = 40), situation, now)
        assertEquals(four, forty)
    }

    @Test
    fun anEXACTsubjectMatchOutranksABroaderOne() = runTest {
        val store = FakeAtomStore(
            mutableListOf(
                atom("General deploy advice", AtomKind.DECISION, subject = "deploy"),
                atom("Specific P30 advice", AtomKind.DECISION, subject = "deploy:android/p30"),
            ),
        )
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 2))
        assertEquals("Specific P30 advice", out.atoms.first().text)
    }

    @Test
    fun aStaleFactIsStillRETURNEDcarryingTheDoubt() = runTest {
        // The kinds are weights, not gates. The agent is being told, not
        // handcuffed - and a fact that failed its check is still evidence.
        val store = FakeAtomStore(
            mutableListOf(atom("The port is 8080", AtomKind.FACT, verifiedOk = false)),
        )
        val out = Recall(store).forSituation(situation)
        assertEquals(1, out.atoms.size)
        assertTrue(out.atoms.first().isStale)
    }

    @Test
    fun butAStaleFactRanksBELOWaSoundOne() = runTest {
        val r = Recall(FakeAtomStore())
        val sound = r.score(atom("x", AtomKind.FACT, verifiedOk = true), situation, now)
        val stale = r.score(atom("x", AtomKind.FACT, verifiedOk = false), situation, now)
        assertTrue(stale < sound)
    }

    @Test
    fun toneIsLoadedByKINDratherThanBySituation() = runTest {
        // "Blunt, hates being asked twice" applies to deploying exactly as much
        // as to anything else - it describes the PERSON, not the subject. Filed
        // under its own topic it simply never matched, so the manner vanished
        // the moment the work got specific, which is when it matters most.
        val store = FakeAtomStore(
            mutableListOf(
                atom("Answer first, explain after", AtomKind.RELATIONSHIP, subject = "something-else"),
                atom("Never use adb push", AtomKind.RULING),
            ),
        )
        val out = Recall(store).forSituation(situation)
        assertEquals(1, out.tone.size)
        assertEquals("Answer first, explain after", out.tone.first().text)
    }

    @Test
    fun toneIsKeptOUTofTheAtomsListSoItIsNotCountedTwice() = runTest {
        val store = FakeAtomStore(
            mutableListOf(atom("Answer first", AtomKind.RELATIONSHIP)),
        )
        val out = Recall(store).forSituation(situation)
        assertTrue(out.atoms.none { it.kind == AtomKind.RELATIONSHIP })
    }

    @Test
    fun toneAloneStillComesBackWhenNothingElseMatched() = runTest {
        val store = FakeAtomStore(mutableListOf(atom("Answer first", AtomKind.RELATIONSHIP)))
        val out = Recall(store).forSituation(situation)
        assertFalse(out.any)
        assertEquals(1, out.tone.size)
        assertEquals(0, out.considered)
    }

    @Test
    fun theAtomBudgetIsRespected() = runTest {
        val store = FakeAtomStore((1..20).map { atom("atom " + it) }.toMutableList())
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 3))
        assertEquals(3, out.atoms.size)
        assertEquals(20, out.considered)
    }

    @Test
    fun aSingleLONGatomDoesNotStarveThreeShortOnes() = runTest {
        // Skipped, not stopped at. The next one may well fit, and three short
        // atoms together are usually worth more than one long one.
        val store = FakeAtomStore(
            mutableListOf(
                atom("s1", AtomKind.RULING),
                atom("x".repeat(500), AtomKind.RULING),
                atom("s2", AtomKind.RULING),
                atom("s3", AtomKind.RULING),
            ),
        )
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 5, maxCharacters = 60))
        assertTrue(out.atoms.size >= 3, "the long atom ate the budget: " + out.atoms.map { it.text.length })
        assertTrue(out.atoms.none { it.text.length == 500 })
    }

    @Test
    fun oneAtomLongerThanTheWholeBudgetIsStillReturned() = runTest {
        // Better a long answer than an empty one.
        val store = FakeAtomStore(mutableListOf(atom("x".repeat(5000), AtomKind.RULING)))
        val out = Recall(store).forSituation(situation, RecallBudget(maxAtoms = 5, maxCharacters = 60))
        assertEquals(1, out.atoms.size)
    }

    @Test
    fun whatHasFADEDisNotOfferedButIsNotGone() = runTest {
        val old = atom("An old decision", AtomKind.DECISION, recorded = now.minus(3650, ChronoUnit.DAYS))
        val store = FakeAtomStore(mutableListOf(old))
        val wear = MemoryWear()
        val out = Recall(store, wear).forSituation(situation)
        assertTrue(out.atoms.isEmpty(), "a ten-year-old decision was still volunteered")
        // Still there by id: fading is what recall offers, not what the store holds.
        assertNotNull(store.get(old.id))
    }

    @Test
    fun bringingSomethingToMindIsWhatMakesItSTICK() = runTest {
        val a = atom("Never use adb push", AtomKind.RULING)
        val store = FakeAtomStore(mutableListOf(a))
        val wear = MemoryWear()
        Recall(store, wear).forSituation(situation)
        assertEquals(1, wear.forAtom(a.id)!!.retrievals)
    }

    @Test
    fun onlyWhatWasHANDEDBACKcountsAsRemembered() = runTest {
        // An atom that matched and lost on ranking was not remembered, it was
        // passed over.
        val winner = atom("Never use adb push", AtomKind.RULING)
        val loser = atom("I like the short form", AtomKind.PREFERENCE)
        val store = FakeAtomStore(mutableListOf(winner, loser))
        val wear = MemoryWear()
        Recall(store, wear).forSituation(situation, RecallBudget(maxAtoms = 1))
        assertNotNull(wear.forAtom(winner.id))
        assertNull(wear.forAtom(loser.id), "an atom that lost on ranking was marked as retrieved")
    }

    @Test
    fun recallWorksWithNoWearAtAll() = runTest {
        val store = FakeAtomStore(mutableListOf(atom("Never use adb push", AtomKind.RULING)))
        val out = Recall(store).forSituation(situation)
        assertEquals(1, out.atoms.size)
    }
}
