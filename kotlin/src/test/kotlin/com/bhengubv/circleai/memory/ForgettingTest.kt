package com.bhengubv.circleai.memory

import java.time.Duration
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class ForgettingTest {

    private val now: Instant = Instant.ofEpochSecond(1_782_896_400L)

    private fun days(n: Long) = Duration.ofDays(n)

    private fun atom(
        kind: AtomKind = AtomKind.DECISION,
        corrections: Int = 0,
        recorded: Instant = now,
    ) = MemoryAtom(kind = kind, corrections = corrections, recordedAtUtc = recorded)

    @Test
    fun retrievabilityStartsAtOneAndDecaysTowardsZero() {
        assertEquals(1.0, Forgetting.retrievability(90.0, Duration.ZERO))
        assertTrue(Forgetting.retrievability(90.0, days(90)) < 0.4)
        assertTrue(Forgetting.retrievability(90.0, days(1000)) < 0.001)
    }

    @Test
    fun moreStabilityMeansSlowerDecay() {
        val fresh = Forgetting.retrievability(90.0, days(180))
        val deep = Forgetting.retrievability(900.0, days(180))
        assertTrue(deep > fresh, "a more deeply learned atom faded faster")
    }

    @Test
    fun zeroStabilityIsAlreadyGoneRatherThanDividingByZero() {
        assertEquals(0.0, Forgetting.retrievability(0.0, Duration.ZERO))
        assertEquals(0.0, Forgetting.retrievability(-5.0, days(1)))
    }

    @Test
    fun timeNeverRUNSBACKWARDSforTheCurve() {
        // A clock that went backwards must not make something MORE retrievable
        // than the moment it was learned.
        assertEquals(1.0, Forgetting.retrievability(90.0, days(-30)))
    }

    @Test
    fun retrievingSomethingNEARLYFORGOTTENstrengthensItFarMore() {
        // The spacing effect, and the reason there are two strengths instead of
        // one score. It falls out of the arithmetic rather than being bolted on.
        val fresh = Forgetting.strengthened(90.0, 1.0)
        val faded = Forgetting.strengthened(90.0, 0.05)
        assertTrue(faded > fresh * 2, "a retrieval at the edge of fading gained no more than a fresh one")
        assertEquals(90.0, fresh, "a retrieval of something perfectly fresh should gain nothing")
    }

    @Test
    fun stabilityONLYEVERGROWS() {
        // The whole point of two factors. A strengthening pass must never make
        // something less learned than a brand new atom.
        assertTrue(Forgetting.strengthened(10.0, 1.0) >= Forgetting.INITIAL_STABILITY_DAYS)
        assertTrue(Forgetting.strengthened(500.0, 0.5) >= 500.0)
    }

    @Test
    fun anOutOfRangeRetrievabilityIsClampedRatherThanInverted() {
        // A negative would produce a gain above the ceiling and a value above 1
        // would produce a LOSS, which stability is not allowed to have.
        assertTrue(Forgetting.strengthened(90.0, 2.0) >= 90.0)
        assertTrue(Forgetting.strengthened(90.0, -1.0) <= 90.0 * (1.0 + Forgetting.SPACING_GAIN))
    }

    @Test
    fun aCORRECTEDatomStartsOutMoreDeeplyLearned() {
        assertTrue(Forgetting.initialStability(atom(corrections = 3)) > Forgetting.initialStability(atom()))
    }

    @Test
    fun theCorrectionBonusIsCAPPEDsoNothingBecomesImmortal() {
        assertEquals(
            Forgetting.initialStability(atom(corrections = 6)),
            Forgetting.initialStability(atom(corrections = 60)),
        )
    }

    @Test
    fun aRuleDoesNotFadeBecauseNOBODYMENTIONEDitLately() {
        // That is exactly when a rule gets broken. A standing instruction has a
        // floor; a decision or a fact does not.
        assertEquals(0.40, Forgetting.floorFor(AtomKind.RULING))
        assertEquals(0.40, Forgetting.floorFor(AtomKind.RELATIONSHIP))
        assertEquals(0.20, Forgetting.floorFor(AtomKind.PREFERENCE))
        assertEquals(0.00, Forgetting.floorFor(AtomKind.DECISION))
        assertEquals(0.00, Forgetting.floorFor(AtomKind.FACT))
    }

    @Test
    fun aTenYearOldRulingIsStillOffered() {
        val old = atom(kind = AtomKind.RULING, recorded = now.minus(3650, ChronoUnit.DAYS))
        assertFalse(Forgetting.faded(old, null, now))
        assertTrue(Forgetting.reach(old, null, now) >= 0.40)
    }

    @Test
    fun aTenYearOldDecisionHasFADEDoutOfWhatIsOffered() {
        val old = atom(kind = AtomKind.DECISION, recorded = now.minus(3650, ChronoUnit.DAYS))
        assertTrue(Forgetting.faded(old, null, now))
    }

    @Test
    fun theClockStartsAtTheLastCORRECTIONwhenThereWasOne() {
        // Being corrected is a stronger event than being filed, so an old atom
        // corrected yesterday is fresh, not stale.
        val corrected = MemoryAtom(
            kind = AtomKind.DECISION,
            recordedAtUtc = now.minus(3650, ChronoUnit.DAYS),
            lastCorrectedUtc = now.minus(1, ChronoUnit.DAYS),
        )
        assertFalse(Forgetting.faded(corrected, null, now))
    }

    @Test
    fun aTraceOVERRIDESboth() {
        // Retrieved here yesterday beats anything the atom says about itself.
        val old = atom(kind = AtomKind.DECISION, recorded = now.minus(3650, ChronoUnit.DAYS))
        val trace = MemoryTrace(5, now.minus(1, ChronoUnit.DAYS), 400.0)
        assertFalse(Forgetting.faded(old, trace, now))
    }
}

class MemoryWearTest {

    private val now: Instant = Instant.ofEpochSecond(1_782_896_400L)

    private fun atom(kind: AtomKind = AtomKind.DECISION) =
        MemoryAtom(kind = kind, recordedAtUtc = now)

    @Test
    fun anUntouchedWearKnowsNothingAndSaysSo() {
        val w = MemoryWear()
        assertEquals(0, w.count)
        assertNull(w.forAtom(UUID.randomUUID()))
        assertFalse(w.isDirty)
    }

    @Test
    fun aRetrievalCountsAndStampsTheTime() {
        val w = MemoryWear()
        val a = atom()
        w.retrieved(a, now)
        val t = w.forAtom(a.id)!!
        assertEquals(1, t.retrievals)
        assertEquals(now, t.lastRetrievedUtc)
        assertTrue(w.isDirty)
    }

    @Test
    fun theReachIsMeasuredBEFOREtheTraceIsUpdated() {
        // Measure it after and every retrieval looks fresh, so nothing ever
        // gains anything and the spacing effect quietly does not exist.
        val w = MemoryWear()
        val a = atom()
        w.retrieved(a, now)
        val first = w.forAtom(a.id)!!.stabilityDays

        // A second retrieval a long time later, when it had nearly faded, must
        // gain far more than one taken immediately.
        val w2 = MemoryWear()
        w2.retrieved(a, now)
        w2.retrieved(a, now.plus(2000, ChronoUnit.DAYS))
        assertTrue(w2.forAtom(a.id)!!.stabilityDays > first * 2)

        val w3 = MemoryWear()
        w3.retrieved(a, now)
        w3.retrieved(a, now.plusSeconds(1))
        assertTrue(
            w3.forAtom(a.id)!!.stabilityDays < w2.forAtom(a.id)!!.stabilityDays,
            "two retrievals a second apart gained as much as one at the edge of fading",
        )
    }

    @Test
    fun retrievalsAccumulate() {
        val w = MemoryWear()
        val a = atom()
        repeat(3) { w.retrieved(a, now.plus(it.toLong(), ChronoUnit.DAYS)) }
        assertEquals(3, w.forAtom(a.id)!!.retrievals)
    }

    @Test
    fun aBatchMarksEveryAtomInIt() {
        val w = MemoryWear()
        val atoms = List(4) { atom() }
        w.retrieved(atoms, now)
        assertEquals(4, w.count)
    }

    @Test
    fun aRetrievedAtomIsHarderToFadeThanAnUntouchedOne() {
        val w = MemoryWear()
        val old = MemoryAtom(kind = AtomKind.DECISION, recordedAtUtc = now.minus(3650, ChronoUnit.DAYS))
        assertTrue(w.faded(old, now), "an untouched ten-year-old decision should have faded")
        w.retrieved(old, now)
        assertFalse(w.faded(old, now), "retrieving it did not bring it back")
    }

    @Test
    fun clearingIsIdempotentAndOnlyDirtiesWhenSomethingWasThere() {
        val w = MemoryWear()
        w.clear()
        assertFalse(w.isDirty)
        w.retrieved(atom(), now)
        w.markClean()
        w.clear()
        assertTrue(w.isDirty)
        assertEquals(0, w.count)
    }

    @Test
    fun aSnapshotRestoresRowForRow() {
        val w = MemoryWear()
        val a = atom()
        w.retrieved(a, now)
        val restored = MemoryWear()
        restored.restore(w.snapshot())
        assertEquals(w.count, restored.count)
        assertEquals(w.forAtom(a.id), restored.forAtom(a.id))
        assertFalse(restored.isDirty, "a freshly loaded wear file should not need writing back")
    }
}

class MemoryFolderTest {

    @Test
    fun everyMachineGetsItsOWNlogBecauseOneWriterCannotConflict() {
        val dir = createTempDir()
        val a = MemoryFolder(dir, "linux-build")
        val b = MemoryFolder(dir, "windows-dev")
        assertTrue(a.ownLog != b.ownLog)
        assertTrue(a.ownLog.endsWith("atoms.linux-build.jsonl"))
    }

    @Test
    fun theIndexIsPerMachineAndDisposable() {
        val dir = createTempDir()
        val f = MemoryFolder(dir, "linux-build")
        assertTrue(f.indexPath.endsWith("index.linux-build.db"))
        assertTrue(f.indexConnectionString.startsWith("jdbc:sqlite:"))
    }

    @Test
    fun aMachineNameThatIdentifiesNOTHINGgetsAMintedIdInstead() {
        // Every Android device reports localhost, so two phones would both call
        // themselves android-localhost and append to ONE log - the merge problem
        // this whole layout exists to avoid, arriving through the front door.
        val dir = createTempDir()
        val f = MemoryFolder(dir, "android-unnamed")
        assertFalse(f.machine.endsWith(MemoryFolder.ANONYMOUS))
        assertTrue(f.machine.startsWith("android-"))
        assertTrue(f.machine.length > "android-".length)
    }

    @Test
    fun theMintedIdIsSTABLEacrossRunsInTheSameFolder() {
        val dir = createTempDir()
        val first = MemoryFolder(dir, "android-unnamed").machine
        val second = MemoryFolder(dir, "android-unnamed").machine
        assertEquals(first, second, "the machine id was re-minted and the logs would split")
    }

    @Test
    fun localhostAndUnknownAreBothTreatedAsAnonymous() {
        assertTrue(MemoryFolder.defaultMachineName("localhost", "Linux").endsWith(MemoryFolder.ANONYMOUS))
        assertTrue(MemoryFolder.defaultMachineName("UNKNOWN", "Linux").endsWith(MemoryFolder.ANONYMOUS))
        assertTrue(MemoryFolder.defaultMachineName("   ", "Linux").endsWith(MemoryFolder.ANONYMOUS))
        assertEquals("linux-buildbox", MemoryFolder.defaultMachineName("buildbox", "Linux"))
    }

    @Test
    fun thePlatformIsPartOfTheName() {
        assertTrue(MemoryFolder.defaultMachineName("box", "Windows 11").startsWith("windows-"))
        assertTrue(MemoryFolder.defaultMachineName("box", "Mac OS X").startsWith("mac-"))
        assertTrue(MemoryFolder.defaultMachineName("box", "Linux").startsWith("linux-"))
        assertTrue(MemoryFolder.defaultMachineName("box", "Plan 9").startsWith("other-"))
    }

    @Test
    fun aMachineNameIsSanitisedIntoSomethingThatCanBeAFileName() {
        val dir = createTempDir()
        assertEquals("my-box-2", MemoryFolder(dir, "My Box/2").machine)
        assertEquals("unknown", MemoryFolder(dir, "///").machine)
    }

    @Test
    fun listingLogsIsStableSoARebuildIsReproducible() {
        val dir = createTempDir()
        val f = MemoryFolder(dir, "linux-build")
        java.io.File(dir, "atoms.zebra.jsonl").writeText("")
        java.io.File(dir, "atoms.alpha.jsonl").writeText("")
        java.io.File(dir, "notes.txt").writeText("")
        val logs = f.allLogs.map { java.io.File(it).name }
        assertEquals(listOf("atoms.alpha.jsonl", "atoms.zebra.jsonl"), logs)
    }

    @Test
    fun theGitIgnoreKeepsTheDerivedAndTheLOCALoutOfTheRepo() {
        // The index is rebuildable; wear is this machine habits and syncing it
        // would put one machine in charge of what another finds easy to recall.
        val dir = createTempDir()
        val f = MemoryFolder(dir, "linux-build")
        f.ensureGitIgnore()
        val text = java.io.File(dir, ".gitignore").readText()
        assertTrue(text.contains("index.*.db"))
        assertTrue(text.contains("wear.*.json"))
        assertTrue(text.contains(".machine-id"))
        assertFalse(text.contains("atoms."), "the logs themselves must be committed")
    }

    @Test
    fun anExistingGitIgnoreIsNotOVERWRITTEN() {
        val dir = createTempDir()
        java.io.File(dir, ".gitignore").writeText("mine")
        MemoryFolder(dir, "linux-build").ensureGitIgnore()
        assertEquals("mine", java.io.File(dir, ".gitignore").readText())
    }

    @Test
    fun aBlankPathIsRefused() {
        kotlin.test.assertFailsWith<IllegalArgumentException> { MemoryFolder("  ") }
    }

    private fun createTempDir(): String {
        val f = java.io.File.createTempFile("memfolder", "")
        f.delete()
        f.mkdirs()
        f.deleteOnExit()
        return f.absolutePath
    }
}
