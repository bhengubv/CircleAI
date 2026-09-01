package com.bhengubv.circleai.memory

import java.io.File
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

class AtomLogTest {

    private val now: Instant = Instant.parse("2026-07-01T09:00:00Z")

    private fun tempDir(): String {
        val f = File.createTempFile("atomlog", "")
        f.delete(); f.mkdirs(); f.deleteOnExit()
        return f.absolutePath
    }

    private fun atom(
        text: String = "Never use adb push to install",
        kind: AtomKind = AtomKind.RULING,
        recorded: Instant = now,
    ) = MemoryAtom(kind = kind, text = text, subject = "deploy", recordedAtUtc = recorded)

    @Test
    fun aLineIsPlainJsonAPersonCanRead() {
        val dir = tempDir()
        val log = AtomLog(MemoryFolder(dir, "linux-build"))
        log.append(atom())
        val line = File(dir, "atoms.linux-build.jsonl").readLines().first()
        assertTrue(line.startsWith("{"))
        assertContains(line, "\"text\"")
        assertContains(line, "Never use adb push")
        assertContains(line, "\"machine\":\"linux-build\"")
    }

    @Test
    fun theCharactersSOMEBODYTYPEDareWrittenNotEscaped() {
        // Half the reason this is text is that a person can open it and read
        // it. An escaping encoder turns isiZulu, Amharic or Japanese into runs
        // of backslash-u and the file stops being readable at all.
        val dir = tempDir()
        AtomLog(MemoryFolder(dir, "linux-build")).append(atom(text = "Yenza kanjena, ngiyabonga - ありがとう"))
        val line = File(dir, "atoms.linux-build.jsonl").readLines().first()
        assertContains(line, "ありがとう")
        assertContains(line, "ngiyabonga")
        assertFalse(line.contains("\\u"), "the log escaped characters somebody typed")
    }

    @Test
    fun aCorrectionIsANEWlineNamingWhatItSupersedes() {
        // A row in a table can be UPDATEd; a line already written cannot. The
        // forward pointer is derived on replay, and that is what makes two
        // machines logs mergeable by simple concatenation.
        val dir = tempDir()
        val log = AtomLog(MemoryFolder(dir, "linux-build"))
        val first = atom()
        log.append(first)
        val replacement = atom(
            text = "Use dotnet build -t:Install instead",
            recorded = now.plus(1, ChronoUnit.HOURS),
        )
        log.append(replacement, supersedes = first.id)

        val all = log.readAll()
        assertEquals(2, all.size)

        // Nothing was edited: the original line is still there, unchanged, and
        // the correction is a second line that POINTS at it.
        val original = all.single { it.id == AtomLog.compact(first.id) }
        val correction = all.single { it.id == AtomLog.compact(replacement.id) }
        assertNull(original.supersedes)
        assertEquals("Never use adb push to install", original.text)
        assertEquals(AtomLog.compact(first.id), correction.supersedes)
    }

    @Test
    fun aRecordRoundTripsBackToTheAtomItCameFrom() {
        val a = MemoryAtom(
            kind = AtomKind.PREFERENCE,
            text = "Answer first, explain after",
            subject = "style",
            challenge = "why",
            outcome = DecisionOutcome.RESOLVED,
            recordedAtUtc = now,
            verify = "check the README",
            sourceEpisode = UUID.randomUUID(),
        )
        val dir = tempDir()
        val record = AtomLog(MemoryFolder(dir, "linux-build")).append(a)
        val back = AtomLog.rehydrate(record)

        assertEquals(a.id, back.id)
        assertEquals(a.kind, back.kind)
        assertEquals(a.text, back.text)
        assertEquals(a.subject, back.subject)
        assertEquals(a.challenge, back.challenge)
        assertEquals(a.outcome, back.outcome)
        assertEquals(a.recordedAtUtc, back.recordedAtUtc)
        assertEquals(a.verify, back.verify)
        assertEquals(a.sourceEpisode, back.sourceEpisode)
        assertEquals("linux-build", back.machine)
    }

    @Test
    fun theEnumNamesAreWrittenTheWayTheCsharpWritesThem() {
        // The log is SHARED with the C#. A Kotlin SCREAMING_CASE name in the
        // file would be unreadable to it, and the two would stop agreeing about
        // what kind of thing an atom is.
        assertEquals("Decision", AtomLog.pascal("DECISION"))
        assertEquals("Relationship", AtomLog.pascal("RELATIONSHIP"))
        assertEquals("CoverLetter", AtomLog.pascal("COVER_LETTER"))
        assertEquals("DECISION", AtomLog.underscore("Decision"))
        assertEquals("COVER_LETTER", AtomLog.underscore("CoverLetter"))
    }

    @Test
    fun everyKindAndOutcomeSurvivesTheRoundTrip() {
        val dir = tempDir()
        val log = AtomLog(MemoryFolder(dir, "linux-build"))
        for (kind in AtomKind.entries) {
            for (outcome in DecisionOutcome.entries + listOf(null)) {
                val a = MemoryAtom(kind = kind, outcome = outcome, text = "x", recordedAtUtc = now)
                val back = AtomLog.rehydrate(log.append(a))
                assertEquals(kind, back.kind, "kind " + kind + " did not survive")
                assertEquals(outcome, back.outcome, "outcome " + outcome + " did not survive")
            }
        }
    }

    @Test
    fun theIdIsTheThirtyTwoCharacterFormTheCsharpUses() {
        val id = UUID.randomUUID()
        val compact = AtomLog.compact(id)
        assertEquals(32, compact.length)
        assertFalse(compact.contains("-"))
        assertEquals(id, AtomLog.parseCompact(compact))
        assertNull(AtomLog.parseCompact("too-short"))
        assertNull(AtomLog.parseCompact("z".repeat(32)))
    }

    @Test
    fun replayOrdersByTIMEacrossEveryMachineLog() {
        // A correction made on the Mac has to supersede a decision made on
        // Windows the same way it would have locally.
        val dir = tempDir()
        AtomLog(MemoryFolder(dir, "windows-dev")).append(atom(text = "written on windows first", recorded = now))
        AtomLog(MemoryFolder(dir, "mac-build")).append(
            atom(text = "written on the mac later", recorded = now.plus(1, ChronoUnit.HOURS)),
        )
        val all = AtomLog(MemoryFolder(dir, "linux-build")).readAll()
        assertEquals(2, all.size)
        assertEquals("written on windows first", all[0].text)
        assertEquals("written on the mac later", all[1].text)
    }

    @Test
    fun anIdenticalTIMESTAMPordersTheSameOnEveryMachine() {
        // Two records at the same instant must not order differently depending
        // on which machine read them, or a rebuild produces a different memory
        // on each box.
        val dir = tempDir()
        AtomLog(MemoryFolder(dir, "windows-dev")).append(atom(text = "from windows", recorded = now))
        AtomLog(MemoryFolder(dir, "mac-build")).append(atom(text = "from mac", recorded = now))

        val a = AtomLog(MemoryFolder(dir, "linux-build")).readAll().map { it.text }
        val b = AtomLog(MemoryFolder(dir, "mac-build")).readAll().map { it.text }
        assertEquals(a, b)
        assertEquals(listOf("from mac", "from windows"), a)
    }

    @Test
    fun anUnreadableLineCostsONLYITSELF() {
        // One truncated write must not cost every memory in the file behind it.
        val dir = tempDir()
        val log = AtomLog(MemoryFolder(dir, "linux-build"))
        log.append(atom(text = "the first one that was written"))
        File(dir, "atoms.linux-build.jsonl").appendText("{\"id\": truncated\n")
        log.append(atom(text = "the third one that was written"))

        val all = log.readAll()
        assertEquals(2, all.size)
    }

    @Test
    fun aRecordWithNoIdIsSkipped() {
        val dir = tempDir()
        File(dir, "atoms.linux-build.jsonl").writeText("{\"text\":\"no id here\",\"recorded\":\"2026-07-01T09:00:00Z\"}\n")
        assertTrue(AtomLog(MemoryFolder(dir, "linux-build")).readAll().isEmpty())
    }

    @Test
    fun anInterruptedLineDoesNotSWALLOWtheNextRecord() {
        // A half-written line with no trailing newline would otherwise absorb
        // whatever is appended next, losing both.
        val dir = tempDir()
        val folder = MemoryFolder(dir, "linux-build")
        File(folder.ownLog).writeText("{\"id\":\"" + "a".repeat(32) + "\",\"text\":\"half a line")
        AtomLog(folder).append(atom(text = "the one written afterwards"))

        val lines = File(folder.ownLog).readLines().filter { it.isNotBlank() }
        assertEquals(2, lines.size, "the append ran into the truncated line")
        assertTrue(lines[1].startsWith("{"))
    }

    @Test
    fun anUnknownFIELDdoesNotMakeALineUnreadable() {
        // A newer machine writing an extra key must not make its lines
        // unreadable to an older one; the log crosses between machines that are
        // not always on the same version.
        val dir = tempDir()
        File(dir, "atoms.future.jsonl").writeText(
            "{\"id\":\"" + "a".repeat(32) + "\",\"text\":\"from a newer build\"," +
                "\"recorded\":\"2026-07-01T09:00:00Z\",\"machine\":\"future\",\"weather\":\"sunny\"}\n",
        )
        val all = AtomLog(MemoryFolder(dir, "linux-build")).readAll()
        assertEquals(1, all.size)
        assertEquals("from a newer build", all[0].text)
    }

    @Test
    fun aBrokenTimestampSortsFIRSTratherThanThrowing() {
        val dir = tempDir()
        File(dir, "atoms.odd.jsonl").writeText(
            "{\"id\":\"" + "a".repeat(32) + "\",\"text\":\"broken date\",\"recorded\":\"not a date\"}\n",
        )
        AtomLog(MemoryFolder(dir, "linux-build")).append(atom(text = "a good one"))
        val all = AtomLog(MemoryFolder(dir, "linux-build")).readAll()
        assertEquals(2, all.size)
        assertEquals("broken date", all[0].text)
    }

    @Test
    fun anEmptyFolderReadsAsNoRecords() {
        assertTrue(AtomLog(MemoryFolder(tempDir(), "linux-build")).readAll().isEmpty())
    }
}

class AtomLearnerTest {

    private val now: Instant = Instant.parse("2026-07-01T09:00:00Z")

    private fun episode(said: String, at: Instant = now) = EpisodicMemoryEntry(
        id = UUID.randomUUID().toString(),
        userId = "u1",
        content = said,
        embedding = FloatArray(0),
        userText = said,
        recordedAtUtc = at,
    )

    private val rule = "Never use adb push to install, it keeps the old data."
    private val soft = "I like the shorter form of that command better."

    @Test
    fun somethingCERTAINisRecorded() = runTest {
        val kept = mutableListOf<MemoryAtom>()
        val report = AtomLearner().learn(listOf(episode(rule)), { kept.add(it) }, emptyList())
        assertEquals(1, report.recorded.size)
        assertEquals(1, kept.size)
        assertTrue(report.offered.isEmpty())
    }

    @Test
    fun somethingBELOWtheBarIsOFFEREDratherThanKept() = runTest {
        // Extraction proposes; it does not decide. Writing a faint reading
        // silently is how a memory fills with noise, and noise ranks.
        val kept = mutableListOf<MemoryAtom>()
        val report = AtomLearner().learn(listOf(episode(soft)), { kept.add(it) }, emptyList())
        assertTrue(kept.isEmpty(), "a candidate below the bar was written anyway")
        assertEquals(1, report.offered.size)
        assertFalse(report.offered[0].certain)
    }

    @Test
    fun runningItTWICEisTheSameAsRunningItOnce() = runTest {
        // After a crash, a pull, or simply a second pass. Everything in this
        // class follows from that.
        val kept = mutableListOf<MemoryAtom>()
        val learner = AtomLearner()
        val episodes = listOf(episode(rule))
        learner.learn(episodes, { kept.add(it) }, emptyList())
        val second = learner.learn(episodes, { kept.add(it) }, kept.toList())
        assertEquals(1, kept.size)
        assertEquals(1, second.alreadyKnown.size)
        assertTrue(second.recorded.isEmpty())
    }

    @Test
    fun theSameSentenceTwiceInONEpassIsKeptOnce() = runTest {
        // It is not in any store yet, so the store check cannot catch it.
        val kept = mutableListOf<MemoryAtom>()
        val report = AtomLearner().learn(
            listOf(episode(rule), episode(rule, now.plusSeconds(60))),
            { kept.add(it) },
            emptyList(),
        )
        assertEquals(1, kept.size)
        assertEquals(1, report.alreadyKnown.size)
        assertEquals(2, report.considered)
    }

    @Test
    fun theSentenceKeptIsTheOneSaidFIRST() = runTest {
        // A rebuild has to land on the same atom either way.
        val kept = mutableListOf<MemoryAtom>()
        AtomLearner().learn(
            listOf(episode(rule, now.plus(1, ChronoUnit.DAYS)), episode(rule, now)),
            { kept.add(it) },
            emptyList(),
        )
        assertEquals(1, kept.size)
        assertEquals(now, kept[0].recordedAtUtc)
    }

    @Test
    fun alreadyKnownBEATSnotSureEnough() = runTest {
        // A sentence already remembered is not a question for anybody, however
        // faintly it was spotted; offering it asks somebody to confirm what
        // they already told us.
        val existing = MemoryAtom(text = soft, kind = AtomKind.PREFERENCE)
        val report = AtomLearner().learn(listOf(episode(soft)), { }, listOf(existing))
        assertEquals(1, report.alreadyKnown.size)
        assertTrue(report.offered.isEmpty(), "an already-known sentence was offered for confirmation")
    }

    @Test
    fun theKnownCheckIgnoresPunctuationAndCase() = runTest {
        val existing = MemoryAtom(text = "NEVER USE ADB PUSH TO INSTALL, IT KEEPS THE OLD DATA")
        val report = AtomLearner().learn(listOf(episode(rule)), { }, listOf(existing))
        assertEquals(1, report.alreadyKnown.size)
        assertTrue(report.recorded.isEmpty())
    }

    @Test
    fun theReportCountsEverythingItLookedAt() = runTest {
        val report = AtomLearner().learn(
            listOf(episode(rule + " " + soft)),
            { },
            emptyList(),
        )
        assertEquals(2, report.considered)
        assertEquals(1, report.recorded.size)
        assertEquals(1, report.offered.size)
    }

    @Test
    fun readingSpotsWithoutKeepingAnything() = runTest {
        // Two questions, two answers: an extractor that also committed would
        // make a wrong reading unfalsifiable.
        val learner = AtomLearner()
        val seen = learner.read(episode(rule))
        assertEquals(1, seen.size)
        assertEquals("cues", learner.extractorName)
    }

    @Test
    fun anEmptyBatchIsAnEmptyReportNotAnError() = runTest {
        val report = AtomLearner().learn(emptyList(), { }, emptyList())
        assertEquals(0, report.considered)
        assertTrue(report.recorded.isEmpty())
    }

    @Test
    fun aCustomExtractorIsUsedInsteadOfCues() = runTest {
        val fake = object : IAtomExtractor {
            override val name = "fake"
            override fun extract(episode: EpisodicMemoryEntry, subject: String?) =
                listOf(AtomCandidate(MemoryAtom(text = "invented"), 0.99, "x", "invented"))
        }
        val kept = mutableListOf<MemoryAtom>()
        val learner = AtomLearner(fake)
        learner.learn(listOf(episode("anything at all")), { kept.add(it) }, emptyList())
        assertEquals("fake", learner.extractorName)
        assertEquals("invented", kept.single().text)
    }
}
