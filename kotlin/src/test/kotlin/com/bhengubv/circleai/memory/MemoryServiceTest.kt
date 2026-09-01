package com.bhengubv.circleai.memory

import com.bhengubv.circleai.memory.sql.SqliteAtomStore
import java.io.File
import java.sql.Connection
import java.sql.DriverManager
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class MemorySyncTest {

    private val now: Instant = Instant.parse("2026-07-01T09:00:00Z")
    private val connections = mutableListOf<Connection>()

    @AfterTest
    fun closeAll() {
        for (c in connections) try { c.close() } catch (e: Exception) { }
        connections.clear()
    }

    private fun store(): IAtomStore {
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        return SqliteAtomStore(c)
    }

    private fun tempDir(): String {
        val f = File.createTempFile("memsync", "")
        f.delete(); f.mkdirs(); f.deleteOnExit()
        return f.absolutePath
    }

    private fun atom(
        text: String,
        kind: AtomKind = AtomKind.RULING,
        recorded: Instant = now,
    ) = MemoryAtom(kind = kind, text = text, subject = "deploy", recordedAtUtc = recorded)

    @Test
    fun recordingWritesTheLogAndTheIndexTOGETHER() = runTest {
        val dir = tempDir()
        val sync = MemorySync(MemoryFolder(dir, "linux-build"))
        val s = store()
        sync.record(s, atom("Never use adb push to install"))

        assertEquals(1, s.count())
        assertEquals(1, sync.log.readAll().size)
    }

    @Test
    fun theIndexHoldsWhatTheLOGsaysNotWhatTheCallerPassed() = runTest {
        // Reading the line back is what makes "the index now" and "the index
        // after a rebuild" the same thing without two pieces of code agreeing.
        val dir = tempDir()
        val sync = MemorySync(MemoryFolder(dir, "linux-build"))
        val s = store()
        // The caller left the machine blank; the log stamps it.
        sync.record(s, atom("Never use adb push to install"))
        assertEquals("linux-build", s.all().single().machine)
    }

    @Test
    fun aRebuildFromTheLOGSproducesTheSameMemory() = runTest {
        // The index is disposable: a corrupt one, a schema change, or a machine
        // that has never seen the folder all cost exactly one rebuild.
        val dir = tempDir()
        val sync = MemorySync(MemoryFolder(dir, "linux-build"))
        val first = store()
        sync.record(first, atom("Never use adb push to install"))
        sync.record(first, atom("Always uninstall before deploying", recorded = now.plusSeconds(60)))

        val rebuilt = store()
        val report = MemorySync(MemoryFolder(dir, "linux-build")).rebuild(rebuilt)

        assertEquals(2, report.records)
        assertEquals(2, report.atoms)
        assertEquals(2, report.current)
        assertEquals(1, report.machines)
        assertEquals(first.count(), rebuilt.count())
    }

    @Test
    fun supersedingIsRESOLVEDduringReplay() = runTest {
        // A log line can only point BACKWARDS at what it replaces; the forward
        // pointer the index wants is worked out by walking the records in time
        // order.
        val dir = tempDir()
        val sync = MemorySync(MemoryFolder(dir, "linux-build"))
        val s = store()
        val first = atom("The old way of deploying")
        sync.record(s, first)
        sync.record(s, atom("The new way of deploying", recorded = now.plusSeconds(60)), supersedes = first.id)

        val current = MemorySync(MemoryFolder(dir, "linux-build")).current()
        assertEquals(1, current.size)
        assertEquals("The new way of deploying", current.single().text)

        // And the superseded one is still in the replay, carrying its pointer.
        val all = MemorySync(MemoryFolder(dir, "linux-build")).replay().atoms
        assertEquals(2, all.size)
        assertNotNull(all.single { it.id == first.id }.supersededBy)
    }

    @Test
    fun aCorrectionMadeOnTheMACappliesToADecisionMadeOnWINDOWS() = runTest {
        // They are just two lines in one ordered stream, which is the whole
        // reason superseding is resolved on replay rather than in the log.
        val dir = tempDir()
        val first = atom("The old way of deploying")
        MemorySync(MemoryFolder(dir, "windows-dev")).record(store(), first)
        MemorySync(MemoryFolder(dir, "mac-build")).record(
            store(),
            atom("The new way of deploying", recorded = now.plus(1, ChronoUnit.HOURS)),
            supersedes = first.id,
        )

        val replay = MemorySync(MemoryFolder(dir, "linux-build")).replay()
        assertEquals(2, replay.machines)
        assertEquals(1, replay.atoms.count { it.isCurrent })
        assertEquals("The new way of deploying", replay.atoms.single { it.isCurrent }.text)
    }

    @Test
    fun theCorrectionCountCarriesDOWNTHECHAIN() = runTest {
        // An atom corrected on three machines reads as corrected three times
        // rather than once each - which is what makes a much-argued rule
        // outrank a fresh one.
        val dir = tempDir()
        val s = store()
        val a = atom("version one of the rule")
        MemorySync(MemoryFolder(dir, "m1")).record(s, a)
        val b = atom("version two of the rule", recorded = now.plusSeconds(60))
        MemorySync(MemoryFolder(dir, "m2")).record(s, b, supersedes = a.id)
        val c = atom("version three of the rule", recorded = now.plusSeconds(120))
        MemorySync(MemoryFolder(dir, "m3")).record(s, c, supersedes = b.id)

        val current = MemorySync(MemoryFolder(dir, "m1")).current().single()
        assertEquals("version three of the rule", current.text)
        assertEquals(2, current.corrections)
        assertNotNull(current.lastCorrectedUtc)
    }

    @Test
    fun anEmptyFolderRebuildsToNothingWithoutComplaint() = runTest {
        val report = MemorySync(MemoryFolder(tempDir(), "linux-build")).rebuild(store())
        assertEquals(SyncReport(0, 0, 0, 0), report)
    }
}

class MemoryServiceTest {

    private val connections = mutableListOf<Connection>()

    @AfterTest
    fun closeAll() {
        for (c in connections) try { c.close() } catch (e: Exception) { }
        connections.clear()
    }

    private fun tempDir(): String {
        val f = File.createTempFile("memservice", "")
        f.delete(); f.mkdirs(); f.deleteOnExit()
        return f.absolutePath
    }

    private fun service(dir: String = tempDir()): MemoryService {
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        return MemoryService(dir, SqliteAtomStore(c), machine = "linux-build")
    }

    @Test
    fun rememberingGoesStraightThroughToTheLOG() = runTest {
        // Nothing is queued, so nothing is lost when the app goes away - which
        // on a phone is the ordinary case rather than the exception.
        val dir = tempDir()
        val s = service(dir)
        s.remember(MemoryAtom(kind = AtomKind.RULING, text = "Never use adb push to install"))
        assertEquals(1, s.count())
        assertEquals(1, s.log.readAll().size)
        assertTrue(File(dir, "atoms.linux-build.jsonl").exists())
    }

    @Test
    fun recallFindsWhatWasRemembered() = runTest {
        val s = service()
        s.remember(
            MemoryAtom(kind = AtomKind.RULING, text = "Never use adb push to install", subject = "deploy"),
        )
        val out = s.recall(Situation(verb = "deploy", target = "android"))
        assertEquals(1, out.atoms.size)
        assertContains(out.atoms.single().text, "adb push")
    }

    @Test
    fun learningFilesWhatWasSAIDandOnlyWhatIsCertain() = runTest {
        val s = service()
        val report = s.learn(
            "Never use adb push to install, it keeps the old data. " +
                "I like the shorter form of that command better.",
            "deploy",
        )
        assertEquals(2, report.considered)
        assertEquals(1, report.recorded.size)
        assertEquals(1, report.offered.size)
        assertEquals(1, s.count())
    }

    @Test
    fun learningTheSameSentenceTwiceKeepsONEatom() = runTest {
        val s = service()
        val said = "Never use adb push to install, it keeps the old data."
        s.learn(said, "deploy")
        s.learn(said, "deploy")
        assertEquals(1, s.count())
    }

    @Test
    fun learningNothingIsAnEmptyReportNotAnError() = runTest {
        val s = service()
        assertEquals(0, s.learn("   ").considered)
        assertEquals(0, s.count())
    }

    @Test
    fun theGitignoreIsWrittenOnConstructionSoTheIndexNeverGetsCommitted() {
        val dir = tempDir()
        service(dir)
        assertTrue(File(dir, ".gitignore").exists())
    }

    @Test
    fun wearIsFlushedOnTheWayOUTofEveryRecall() = runTest {
        // A force-stop never calls a lifecycle callback, and it is how a phone
        // usually kills an app - holding wear back would take the session
        // familiarity with it.
        val flushes = mutableListOf<Map<UUID, MemoryTrace>>()
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        val s = MemoryService(tempDir(), SqliteAtomStore(c), "linux-build") { flushes.add(it) }

        s.remember(MemoryAtom(kind = AtomKind.RULING, text = "Never use adb push", subject = "deploy"))
        s.recall(Situation(verb = "deploy"))
        assertEquals(1, flushes.size)
        assertEquals(1, flushes.single().size)
    }

    @Test
    fun aRecallThatChangedNothingDoesNotWriteTheWearFile() = runTest {
        val flushes = mutableListOf<Map<UUID, MemoryTrace>>()
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        val s = MemoryService(tempDir(), SqliteAtomStore(c), "linux-build") { flushes.add(it) }

        s.recall(Situation(verb = "deploy"))
        assertTrue(flushes.isEmpty(), "an empty recall wrote the wear file anyway")
    }

    @Test
    fun aRebuildRestoresTheIndexFromTheLogsAlone() = runTest {
        val dir = tempDir()
        val first = service(dir)
        first.remember(MemoryAtom(kind = AtomKind.RULING, text = "Never use adb push", subject = "deploy"))

        // A brand new index over the same folder: cold start, or a corrupt file.
        val second = service(dir)
        assertEquals(0, second.count())
        val report = second.rebuild()
        assertEquals(1, report.records)
        assertEquals(1, second.count())
    }

    @Test
    fun theServiceReportsWhereItLivesAndWhoIsWriting() {
        val dir = tempDir()
        val s = service(dir)
        assertEquals("linux-build", s.machineName)
        assertTrue(s.path.isNotEmpty())
    }
}

class ModuleMemoryTest {

    private val connections = mutableListOf<Connection>()

    @AfterTest
    fun closeAll() {
        for (c in connections) try { c.close() } catch (e: Exception) { }
        connections.clear()
    }

    private fun service(): MemoryService {
        val f = File.createTempFile("modmem", "")
        f.delete(); f.mkdirs(); f.deleteOnExit()
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        return MemoryService(f.absolutePath, SqliteAtomStore(c), machine = "linux-build")
    }

    @Test
    fun aModuleNameIsNormalisedAndRequired() {
        assertEquals("interpret", ModuleMemory(service(), "  Interpret  ").module)
        assertFailsWith<IllegalArgumentException> { ModuleMemory(service(), "   ") }
    }

    @Test
    fun aModuleOWNSwhatItRemembers() = runTest {
        val s = service()
        val m = ModuleMemory(s, "interpret")
        m.remember(MemoryAtom(kind = AtomKind.RULING, text = "Never keep what passes through me"))
        assertEquals("interpret", s.all().single().subject)
    }

    @Test
    fun theSubjectIsPREFIXEDnotReplaced() = runTest {
        // So "interpret:languages" still rolls up to "interpret" and a module
        // whole memory can be read at once.
        val s = service()
        val m = ModuleMemory(s, "interpret")
        m.remember(MemoryAtom(kind = AtomKind.RULING, text = "x", subject = "languages"))
        assertEquals("interpret:languages", s.all().single().subject)
    }

    @Test
    fun anAlreadyOwnedSubjectIsNotPrefixedTWICE() = runTest {
        val s = service()
        val m = ModuleMemory(s, "interpret")
        m.remember(MemoryAtom(kind = AtomKind.RULING, text = "x", subject = "interpret:languages"))
        assertEquals("interpret:languages", s.all().single().subject)
    }

    @Test
    fun aRULESONLYmoduleStillRemembersItsOwnPROHIBITION() = runTest {
        // That is the part that is easy to get backwards. A module with no
        // continuity cannot remember that it must not keep anything.
        val s = service()
        val m = ModuleMemory(s, "interpret", MemoryRetention.RULES_ONLY)
        assertTrue(m.remember(MemoryAtom(kind = AtomKind.RULING, text = "Never keep what passes through me")))
        assertEquals(1, s.count())
    }

    @Test
    fun butARulesOnlyModuleKeepsNONEofTheWORDS() = runTest {
        // A live interpreter must not retain what passes through it: those are
        // two other people words.
        val s = service()
        val m = ModuleMemory(s, "interpret", MemoryRetention.RULES_ONLY)
        assertFalse(m.remember(MemoryAtom(kind = AtomKind.DECISION, text = "what one of them said")))
        assertFalse(m.remember(MemoryAtom(kind = AtomKind.FACT, text = "a fact about one of them")))
        assertEquals(0, s.count())
    }

    @Test
    fun aRulesOnlyModuleDoesNotEvenEXTRACTfromWhatItHeard() = runTest {
        // The words never reach the learner at all.
        val s = service()
        val m = ModuleMemory(s, "interpret", MemoryRetention.RULES_ONLY)
        val report = m.heard("Never use adb push to install, it keeps the old data.")
        assertEquals(0, report.considered)
        assertEquals(0, s.count())
    }

    @Test
    fun aNormalModuleLearnsFromWhatItHeardUnderItsOwnSubject() = runTest {
        val s = service()
        val m = ModuleMemory(s, "deploy")
        val report = m.heard("Never use adb push to install, it keeps the old data.")
        assertEquals(1, report.recorded.size)
        assertEquals("deploy", s.all().single().subject)
    }

    @Test
    fun preferencesAndRelationshipsAreRulesForRetentionPurposes() = runTest {
        // How somebody wants to be worked with is a standing instruction, not a
        // record of what they said.
        val s = service()
        val m = ModuleMemory(s, "interpret", MemoryRetention.RULES_ONLY)
        assertTrue(m.remember(MemoryAtom(kind = AtomKind.PREFERENCE, text = "speak slowly")))
        assertTrue(m.remember(MemoryAtom(kind = AtomKind.RELATIONSHIP, text = "answer first")))
        assertEquals(2, s.count())
    }

    @Test
    fun recallGoesStraightThroughToTheDeviceMemory() = runTest {
        val s = service()
        s.remember(MemoryAtom(kind = AtomKind.RULING, text = "Never use adb push", subject = "deploy"))
        val m = ModuleMemory(s, "somethingelse")
        // A module recalls the DEVICE memory, not only its own corner of it -
        // one memory, many consumers.
        assertEquals(1, m.recall(Situation(verb = "deploy")).atoms.size)
    }
}
