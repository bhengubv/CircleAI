package com.bhengubv.circleai.memory.sql

import com.bhengubv.circleai.memory.AtomKind
import com.bhengubv.circleai.memory.DecisionOutcome
import com.bhengubv.circleai.memory.MemoryAtom
import com.bhengubv.circleai.memory.Situation
import java.sql.Connection
import java.sql.DriverManager
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class SqlDialectTest {

    @Test
    fun eachEngineQuotesIdentifiersItsOwnWay() {
        assertEquals("\"text\"", SqlDialect.postgreSql.quote("text"))
        assertEquals("[text]", SqlDialect.sqlServer.quote("text"))
        assertEquals("`text`", SqlDialect.mySql.quote("text"))
        assertEquals("\"text\"", SqlDialect.oracle.quote("text"))
    }

    @Test
    fun eachEngineMarksParametersItsOwnWay() {
        assertEquals("@q", SqlDialect.postgreSql.parameter("q"))
        assertEquals(":q", SqlDialect.oracle.parameter("q"))
        assertEquals("$" + "q", SqlDialect.sqlite.parameter("q"))
    }

    @Test
    fun sqlServerUsesOFFSETandFETCHsoItCOMPOSESwithAnOrderBy() {
        // TOP would be simpler and would not compose with an ORDER BY the caller
        // wrote rather than one pasted in here.
        assertEquals("OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY", SqlDialect.sqlServer.limit(20))
        assertEquals("FETCH FIRST 20 ROWS ONLY", SqlDialect.oracle.limit(20))
        assertEquals("LIMIT 20", SqlDialect.postgreSql.limit(20))
    }

    @Test
    fun onlyTheEnginesThatNeedNOTHINGINSTALLEDclaimFullText() {
        // Postgres gets a generated tsvector and a GIN index; MySQL gets a
        // FULLTEXT index on InnoDB. SQL Server needs the Full-Text feature
        // installed and Oracle needs a CTXSYS index and a privilege a memory has
        // no business asking for - so both fall back to LIKE, which finds
        // things, rather than throwing at startup because a feature is absent.
        assertTrue(SqlDialect.postgreSql.fullText)
        assertTrue(SqlDialect.mySql.fullText)
        assertFalse(SqlDialect.sqlServer.fullText)
        assertFalse(SqlDialect.oracle.fullText)
        assertFalse(SqlDialect.sqlite.fullText)
    }

    @Test
    fun theLIKEfloorSearchesTextSubjectAndChallenge() {
        val s = SqlDialect.sqlServer.search(listOf("deploy", "android"))
        assertEquals(2, s.parameters.size)
        assertEquals("%deploy%", s.parameters[0].value)
        assertContains(s.where, "[text] LIKE")
        assertContains(s.where, "[challenge] LIKE")
        assertContains(s.where, ") OR (")
    }

    @Test
    fun postgresUsesWebsearchSoPunctuationDoesNotThrow() {
        // Half the memory is about flags like -t:InstallKeepingData, and
        // to_tsquery would throw on every one of them.
        val s = SqlDialect.postgreSql.search(listOf("deploy", "-t:Install"))
        assertContains(s.where, "websearch_to_tsquery")
        assertEquals("deploy or -t:Install", s.parameters.single().value)
    }

    @Test
    fun mysqlQUOTESeveryTermBecauseALeadingHyphenMeansNOT() {
        // Unquoted, "-t:Install" asks MySQL to EXCLUDE the very thing being
        // looked for, and the search comes back empty for no visible reason.
        val s = SqlDialect.mySql.search(listOf("-t:Install"))
        assertEquals("\"-t:Install\"", s.parameters.single().value)
        assertContains(s.where, "IN BOOLEAN MODE")
    }

    @Test
    fun aQuoteInsideATermCannotBreakOutOfTheMysqlQuoting() {
        val s = SqlDialect.mySql.search(listOf("say \"hello\""))
        assertEquals("\"say  hello \"", s.parameters.single().value)
    }

    @Test
    fun everyDialectCanBuildItsOwnTableAndIndexes() {
        for (d in SqlDialect.all) {
            val ddl = d.createTable("atoms")
            assertContains(ddl, "CREATE TABLE", message = d.name)
            assertContains(ddl, d.quote("text_key"), message = d.name)
            assertContains(ddl, d.quote("superseded_by"), message = d.name)
            assertTrue(d.indexes("atoms").isNotEmpty(), d.name + " built no indexes")
            // Case-insensitively: Oracle folds the name, which is correct and
            // is pinned on its own below.
            assertContains(d.tableExists("atoms").lowercase(), "atoms", message = d.name)
        }
    }

    @Test
    fun oracleUppercasesTheTableNameItLooksFor() {
        // user_tables holds folded names, so a lowercase lookup finds nothing
        // and the store rebuilds the schema on every start.
        assertContains(SqlDialect.oracle.tableExists("atoms"), "'ATOMS'")
    }

    @Test
    fun theColumnTypesAreWhatEachEngineActuallyUnderstands() {
        assertContains(SqlDialect.sqlServer.createTable("atoms"), "NVARCHAR(MAX)")
        assertContains(SqlDialect.oracle.createTable("atoms"), "CLOB")
        assertContains(SqlDialect.oracle.createTable("atoms"), "VARCHAR2(")
        assertContains(SqlDialect.postgreSql.createTable("atoms"), "TEXT")
    }
}

class AdoAtomStoreTest {

    private val connections = mutableListOf<Connection>()
    private val now: Instant = Instant.parse("2026-07-01T09:00:00Z")

    private fun store(): AdoAtomStore {
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        return AdoAtomStore(c, SqlDialect.sqlite)
    }

    @AfterTest
    fun closeAll() {
        for (c in connections) try { c.close() } catch (e: Exception) { }
        connections.clear()
    }

    private fun atom(
        text: String = "Never use adb push to install",
        kind: AtomKind = AtomKind.RULING,
        subject: String? = "deploy:android",
        corrections: Int = 0,
        outcome: DecisionOutcome? = null,
        recorded: Instant = now,
    ) = MemoryAtom(
        kind = kind, text = text, subject = subject, corrections = corrections,
        outcome = outcome, recordedAtUtc = recorded, machine = "linux-build",
    )

    @Test
    fun theSchemaIsBuiltOnFirstUseAndNotRebuiltAfterwards() = runTest {
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        val first = AdoAtomStore(c, SqlDialect.sqlite)
        first.add(atom())
        // A second store over the SAME connection must find the table and leave
        // the row alone, not drop and recreate it.
        val second = AdoAtomStore(c, SqlDialect.sqlite)
        assertEquals(1, second.count())
    }

    @Test
    fun anAtomRoundTripsThroughEVERYcolumn() = runTest {
        val s = store()
        val a = MemoryAtom(
            kind = AtomKind.PREFERENCE,
            text = "Answer first, explain after",
            subject = "style",
            sourceEpisode = UUID.randomUUID(),
            recordedAtUtc = now,
            machine = "mac-build",
            corrections = 3,
            lastCorrectedUtc = now.plus(1, ChronoUnit.DAYS),
            challenge = "why does it matter",
            outcome = DecisionOutcome.RESOLVED,
            verify = "check the README",
            verifiedAtUtc = now.plus(2, ChronoUnit.DAYS),
            verifiedOk = true,
        )
        s.add(a)
        val back = s.get(a.id)!!

        assertEquals(a.id, back.id)
        assertEquals(a.kind, back.kind)
        assertEquals(a.text, back.text)
        assertEquals(a.subject, back.subject)
        assertEquals(a.sourceEpisode, back.sourceEpisode)
        assertEquals(a.recordedAtUtc, back.recordedAtUtc)
        assertEquals(a.machine, back.machine)
        assertEquals(a.corrections, back.corrections)
        assertEquals(a.lastCorrectedUtc, back.lastCorrectedUtc)
        assertEquals(a.challenge, back.challenge)
        assertEquals(a.outcome, back.outcome)
        assertEquals(a.verify, back.verify)
        assertEquals(a.verifiedAtUtc, back.verifiedAtUtc)
        assertEquals(true, back.verifiedOk)
    }

    @Test
    fun aNullVerifiedOkStaysNullRatherThanBecomingFalse() = runTest {
        // Never checked is not the same as checked and wrong, and a store that
        // conflates them makes every unverified fact look stale.
        val s = store()
        val a = atom(kind = AtomKind.FACT)
        s.add(a)
        assertNull(s.get(a.id)!!.verifiedOk)
        assertFalse(s.get(a.id)!!.isStale)
    }

    @Test
    fun addingTheSameAtomTWICEisTheSameAsAddingItOnce() = runTest {
        // Upsert is delete-then-insert in a transaction, which is exactly the
        // idempotence a replay needs.
        val s = store()
        val a = atom()
        s.add(a)
        s.add(a.copy(text = "corrected in place"))
        assertEquals(1, s.count())
        assertEquals("corrected in place", s.get(a.id)!!.text)
    }

    @Test
    fun anUnknownIdIsNullNotAnError() = runTest {
        assertNull(store().get(UUID.randomUUID()))
    }

    @Test
    fun supersedingCARRIESTHECOUNTforward() = runTest {
        // Losing the tally throws away the signal that makes a
        // repeatedly-corrected atom outrank a fresh one.
        val s = store()
        val first = atom(corrections = 2)
        s.add(first)
        val carried = s.supersede(first.id, atom(text = "Use dotnet build instead"))
        assertEquals(3, carried.corrections)
        assertNotNull(carried.lastCorrectedUtc)
    }

    @Test
    fun aCorrectionDoesNotRECLASSIFYwhatWasSaid() = runTest {
        // A ruling corrected into a decision would quietly lose its floor and
        // start fading, which is how a standing instruction disappears.
        val s = store()
        val rule = atom(kind = AtomKind.RULING)
        s.add(rule)
        val carried = s.supersede(rule.id, atom(text = "restated", kind = AtomKind.DECISION))
        assertEquals(AtomKind.RULING, carried.kind)
    }

    @Test
    fun aSupersededAtomIsNEVERDELETED() = runTest {
        // It stops being an answer and stays readable, because the history is
        // what gives a current atom its weight.
        val s = store()
        val first = atom()
        s.add(first)
        s.supersede(first.id, atom(text = "the newer version"))

        assertNotNull(s.get(first.id), "the superseded atom was deleted")
        assertFalse(s.get(first.id)!!.isCurrent)
        assertEquals(1, s.count(), "the superseded atom is still being counted as current")
        assertEquals(2, s.all(includeSuperseded = true).size)
        assertEquals(1, s.all(includeSuperseded = false).size)
    }

    @Test
    fun markVerifiedRecordsBothTheAnswerAndWhen() = runTest {
        val s = store()
        val a = atom(kind = AtomKind.FACT)
        s.add(a)
        s.markVerified(a.id, false, now.plus(3, ChronoUnit.DAYS))
        val back = s.get(a.id)!!
        assertEquals(false, back.verifiedOk)
        assertEquals(now.plus(3, ChronoUnit.DAYS), back.verifiedAtUtc)
        assertTrue(back.isStale)
    }

    @Test
    fun matchFindsBySUBJECTfirst() = runTest {
        val s = store()
        s.add(atom(text = "General advice about deploying", subject = "deploy"))
        s.add(atom(text = "Specific advice for android", subject = "deploy:android"))
        s.add(atom(text = "Something about invoices entirely", subject = "billing"))

        val out = s.match(Situation(verb = "deploy", target = "android"), 10)
        assertEquals(2, out.size)
        assertTrue(out.none { it.subject == "billing" })
    }

    @Test
    fun matchFallsBackToKEYWORDSwhenTheSubjectFindsTooLittle() = runTest {
        val s = store()
        s.add(atom(text = "The merlin phone refuses an incremental install", subject = null))
        val out = s.match(Situation(verb = "install", target = "merlin"), 10)
        assertEquals(1, out.size)
    }

    @Test
    fun aSupersededAtomIsNotOFFEREDbyMatch() = runTest {
        val s = store()
        val first = atom(text = "The old way of deploying to android")
        s.add(first)
        s.supersede(first.id, atom(text = "The new way of deploying to android"))
        val out = s.match(Situation(verb = "deploy", target = "android"), 10)
        assertEquals(1, out.size)
        assertEquals("The new way of deploying to android", out[0].text)
    }

    @Test
    fun anAtomIsNotReturnedTWICEwhenTwoKeysBothMatchIt() = runTest {
        // deploy:android and deploy both hit the same row on a walk-up.
        val s = store()
        s.add(atom(subject = "deploy"))
        val out = s.match(Situation(verb = "deploy", target = "android"), 10)
        assertEquals(1, out.size)
    }

    @Test
    fun theLimitIsRespected() = runTest {
        val s = store()
        repeat(10) { s.add(atom(text = "deploy note number " + it, subject = "deploy")) }
        assertEquals(3, s.match(Situation(verb = "deploy"), 3).size)
    }

    @Test
    fun byKindReturnsOnlyThatKindAndOnlyCurrentOnes() = runTest {
        val s = store()
        s.add(atom(kind = AtomKind.RULING, text = "a standing rule about things"))
        s.add(atom(kind = AtomKind.PREFERENCE, text = "a preference about things"))
        val superseded = atom(kind = AtomKind.RULING, text = "an older rule about things")
        s.add(superseded)
        s.supersede(superseded.id, atom(kind = AtomKind.RULING, text = "the newer rule"))

        val rulings = s.byKind(AtomKind.RULING, 50)
        assertEquals(2, rulings.size)
        assertTrue(rulings.all { it.kind == AtomKind.RULING && it.isCurrent })
    }

    @Test
    fun knowsIgnoresCASEandPUNCTUATION() = runTest {
        // Learning asks this of every sentence it spots, on every turn.
        val s = store()
        s.add(atom(text = "Never use adb push to install"))
        assertTrue(s.knows("never   use ADB push to install!"))
        assertFalse(s.knows("something else entirely"))
        assertFalse(s.knows("   "))
    }

    @Test
    fun aSupersededSentenceIsNoLongerKNOWN() = runTest {
        val s = store()
        val a = atom(text = "Never use adb push to install")
        s.add(a)
        s.supersede(a.id, atom(text = "Use dotnet build to install"))
        assertFalse(s.knows("Never use adb push to install"))
        assertTrue(s.knows("Use dotnet build to install"))
    }

    @Test
    fun listingIsNewestFirst() = runTest {
        val s = store()
        s.add(atom(text = "the oldest one written", recorded = now.minus(2, ChronoUnit.DAYS)))
        s.add(atom(text = "the newest one written", recorded = now))
        s.add(atom(text = "the middle one written", recorded = now.minus(1, ChronoUnit.DAYS)))
        assertEquals("the newest one written", s.all().first().text)
    }

    @Test
    fun theEngineNamesItselfAndReportsWhetherFullTextCameUp() {
        val s = store()
        assertEquals("SQLite", s.engine)
        assertFalse(s.fullTextAvailable)
    }

    @Test
    fun aTermTooShortToNarrowAnythingIsDropped() {
        // Two letters match everything; past eight terms a keyword search stops
        // narrowing and starts costing.
        assertEquals(listOf("deploy", "android"), AdoAtomStore.terms("to deploy on android, ok"))
        assertEquals(8, AdoAtomStore.terms((1..20).joinToString(" ") { "term" + it }).size)
        assertEquals(listOf("deploy"), AdoAtomStore.terms("deploy DEPLOY Deploy"))
    }

    @Test
    fun anEmptyStoreAnswersEverythingWithoutComplaint() = runTest {
        val s = store()
        assertEquals(0, s.count())
        assertTrue(s.all().isEmpty())
        assertTrue(s.byKind(AtomKind.RULING).isEmpty())
        assertTrue(s.match(Situation(verb = "deploy"), 10).isEmpty())
    }

    @Test
    fun theCallerAutoCommitSettingIsRESTOREDafterAWrite() = runTest {
        // The connection belongs to them; leaving auto-commit off would silently
        // change how the rest of their application behaves.
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        c.autoCommit = true
        val s = AdoAtomStore(c, SqlDialect.sqlite)
        s.add(atom())
        assertTrue(c.autoCommit, "the store left auto-commit off")
    }

    @Test
    fun theSqliteStoreIsTheSameStoreWithTheSqliteDialect() = runTest {
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        val s = SqliteAtomStore(c)
        val a = atom()
        s.add(a)
        assertEquals(1, s.count())
        assertEquals(a.text, s.get(a.id)!!.text)
    }
}
