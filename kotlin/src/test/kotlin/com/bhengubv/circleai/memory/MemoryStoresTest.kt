package com.bhengubv.circleai.memory

import java.io.File
import java.sql.Connection
import java.sql.DriverManager
import java.time.Instant
import java.util.UUID
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class HookPayloadTest {

    @Test
    fun anEnvelopeGivesUpItsPrompt() {
        assertEquals("deploy the app", HookPayload.promptFrom("{\"prompt\":\"deploy the app\"}"))
        assertEquals("x", HookPayload.promptFrom("  \n {\"session\":\"1\",\"prompt\":\"x\"} "))
    }

    @Test
    fun somethingThatIsNotAnEnvelopeIsTakenAtFACEVALUE()
    {
        // A person piping their own notes in is the other half of what this reads.
        assertEquals("just some words", HookPayload.promptFrom("just some words"))
        assertEquals("a line about {braces}", HookPayload.promptFrom("a line about {braces}"))
    }

    @Test
    fun anEnvelopeWithNOmessageInItIsNOTHING() {
        // Reading the envelope as if it were the message would file field names
        // as things somebody said.
        assertEquals("", HookPayload.promptFrom("{\"session\":\"1\",\"cwd\":\"/tmp\"}"))
        assertEquals("", HookPayload.promptFrom("{}"))
    }

    @Test
    fun aNonStringPromptIsNothingRatherThanItsRendering() {
        assertEquals("", HookPayload.promptFrom("{\"prompt\":42}"))
        assertEquals("", HookPayload.promptFrom("{\"prompt\":null}"))
        assertEquals("", HookPayload.promptFrom("{\"prompt\":{\"nested\":\"x\"}}"))
    }

    @Test
    fun somethingThatStartsWithABraceAndIsNotJsonIsPROSE() {
        // Far more likely than a broken payload.
        val prose = "{ this is not json, it is a note somebody typed"
        assertEquals(prose, HookPayload.promptFrom(prose))
    }

    @Test
    fun nothingInIsNothingOut() {
        assertEquals("", HookPayload.promptFrom(null))
        assertEquals("", HookPayload.promptFrom(""))
        assertEquals("", HookPayload.promptFrom("   "))
    }
}

class InMemoryGoalStoreTest {

    private fun goal(
        id: String = UUID.randomUUID().toString(),
        userId: String = "u1",
        status: GoalStatus = GoalStatus.Active,
    ) = Goal(
        id = id, userId = userId, title = "Ship the port", description = "all eight languages",
        status = status, priority = GoalPriority.High, createdUtc = Instant.EPOCH,
    )

    @Test
    fun upsertThenGetRoundTrips() = runTest {
        val s = InMemoryGoalStore()
        val g = goal()
        s.upsertAsync(g)
        assertEquals(g, s.getAsync(g.id))
    }

    @Test
    fun oneUserGoalsAreNotAnotherUserGoals() = runTest {
        val s = InMemoryGoalStore()
        s.upsertAsync(goal(userId = "u1"))
        s.upsertAsync(goal(userId = "u2"))
        assertEquals(1, s.listAsync("u1").size)
        assertTrue(s.listAsync("u3").isEmpty())
    }

    @Test
    fun onlyActiveGoalsComeBackFromGetActive() = runTest {
        val s = InMemoryGoalStore()
        s.upsertAsync(goal(status = GoalStatus.Active))
        s.upsertAsync(goal(status = GoalStatus.Completed))
        s.upsertAsync(goal(status = GoalStatus.Abandoned))
        assertEquals(1, s.getActiveAsync("u1").size)
        assertEquals(3, s.listAsync("u1").size)
    }

    @Test
    fun upsertReplacesById() = runTest {
        val s = InMemoryGoalStore()
        val g = goal()
        s.upsertAsync(g)
        s.upsertAsync(g.copy(status = GoalStatus.Completed))
        assertEquals(1, s.listAsync("u1").size)
        assertEquals(GoalStatus.Completed, s.getAsync(g.id)!!.status)
    }

    @Test
    fun deleteRemovesItAndAnUnknownIdIsHarmless() = runTest {
        val s = InMemoryGoalStore()
        val g = goal()
        s.upsertAsync(g)
        s.deleteAsync(g.id)
        assertNull(s.getAsync(g.id))
        s.deleteAsync(g.id)
    }

    @Test
    fun aBlankIdOrUserIsRefused() = runTest {
        val s = InMemoryGoalStore()
        assertFailsWith<IllegalArgumentException> { s.listAsync("  ") }
        assertFailsWith<IllegalArgumentException> { s.getAsync("") }
        assertFailsWith<IllegalArgumentException> { s.deleteAsync(" ") }
        assertFailsWith<IllegalArgumentException> { s.getActiveAsync("") }
    }
}

class JsonStoresTest {

    private fun tempDir(): String {
        val f = File.createTempFile("jsonstore", "")
        f.delete(); f.mkdirs(); f.deleteOnExit()
        return f.absolutePath
    }

    @Test
    fun anAffectStateRoundTripsThroughDisk() = runTest {
        val dir = tempDir()
        val s = JsonAffectStore(dir)
        val state = AffectState(userId = "u1", curiosity = 0.9f, rapport = 0.4f, energy = 0.2f)
        s.saveAsync(state)

        val back = JsonAffectStore(dir).loadAsync("u1")
        assertEquals(0.9f, back.curiosity)
        assertEquals(0.4f, back.rapport)
        assertEquals(0.2f, back.energy)
        assertEquals("u1", back.userId)
    }

    @Test
    fun savingStampsWhenItWasSaved() = runTest {
        val s = JsonAffectStore(tempDir())
        val state = AffectState(userId = "u1", lastUpdatedUtc = Instant.EPOCH)
        s.saveAsync(state)
        assertTrue(state.lastUpdatedUtc.isAfter(Instant.EPOCH))
    }

    @Test
    fun anUnknownUserGetsAFRESHstateNotAnError() = runTest {
        val back = JsonAffectStore(tempDir()).loadAsync("nobody")
        assertEquals("nobody", back.userId)
        assertEquals(0.5f, back.curiosity)
    }

    @Test
    fun aCORRUPTfileReadsAsAFreshStateRatherThanThrowing() = runTest {
        // Affect is a running estimate of how a conversation is going. Refusing
        // to start because one file is unreadable trades a lost estimate for a
        // dead app, and the next save overwrites it anyway.
        val dir = tempDir()
        val s = JsonAffectStore(dir)
        s.pathFor("u1").writeText("{ not json at all")
        val back = s.loadAsync("u1")
        assertEquals("u1", back.userId)
        assertEquals(0.5f, back.curiosity)
    }

    @Test
    fun aPersonaRoundTripsWithItsWeightsAndCounters() = runTest {
        val dir = tempDir()
        val p = PersonaState(userId = "u1")
        p.verbosity = "brief"
        p.formality = "formal"
        p.preferredLocale = "zu-ZA"
        p.topicWeights["deploy"] = 0.8f
        p.disfavouredTopics.add("smalltalk")
        p.totalInteractions = 12
        p.positiveSignals = 9
        p.negativeSignals = 3
        p.traitSummary = "blunt, hates being asked twice"
        JsonPersonaStore(dir).saveAsync(p)

        val back = JsonPersonaStore(dir).loadAsync("u1")
        assertEquals("brief", back.verbosity)
        assertEquals("formal", back.formality)
        assertEquals("zu-ZA", back.preferredLocale)
        assertEquals(0.8f, back.topicWeights["deploy"])
        assertTrue(back.disfavouredTopics.contains("smalltalk"))
        assertEquals(12, back.totalInteractions)
        assertEquals("blunt, hates being asked twice", back.traitSummary)
    }

    @Test
    fun aUserIdCannotWriteOUTSIDEtheFolderItWasGiven() {
        // The id becomes part of a file name, and an id with a slash in it would
        // otherwise land somewhere nobody asked for.
        val dir = tempDir()
        val s = JsonAffectStore(dir)
        val path = s.pathFor("../../etc/passwd")
        assertEquals(File(dir).absolutePath, path.parentFile.absolutePath)
        assertFalse(path.name.contains("/"))
        assertFalse(path.name.contains(".."))
    }

    @Test
    fun aBlankDirectoryIsRefused() {
        assertFailsWith<IllegalArgumentException> { JsonAffectStore("  ") }
        assertFailsWith<IllegalArgumentException> { JsonPersonaStore("") }
    }

    @Test
    fun noTemporaryFileIsLeftBehindAfterASave() = runTest {
        // Write-then-rename, and the temporary name is unique per save so two
        // saves for one user cannot contend on one path.
        val dir = tempDir()
        val s = JsonAffectStore(dir)
        repeat(5) { s.saveAsync(AffectState(userId = "u1")) }
        val leftovers = File(dir).listFiles()!!.filter { it.name.endsWith(".tmp") }
        assertTrue(leftovers.isEmpty(), "left temporary files behind: " + leftovers.map { it.name })
    }
}

class SqlMemoryStoresTest {

    private val connections = mutableListOf<Connection>()

    @AfterTest
    fun closeAll() {
        for (c in connections) try { c.close() } catch (e: Exception) { }
        connections.clear()
    }

    private fun conn(): Connection {
        val c = DriverManager.getConnection("jdbc:sqlite::memory:")
        connections.add(c)
        return c
    }

    private fun episode(id: String = UUID.randomUUID().toString(), at: Instant = Instant.EPOCH) =
        EpisodicMemoryEntry(
            id = id,
            userId = "u1",
            content = "hello and the answer",
            embedding = floatArrayOf(0.1f, -0.5f, 0.9f),
            createdUtc = at,
            tags = listOf("deploy", "android"),
            importance = 0.7f,
            userText = "hello",
            assistantText = "the answer",
            appContext = "deploy",
            recordedAtUtc = at,
        )

    @Test
    fun anEpisodeRoundTripsThroughEveryColumn() = runTest {
        val s = SqliteEpisodicStore(conn())
        val e = episode()
        s.save(e)
        val back = s.getRecent("u1", 10).single()

        assertEquals(e.id, back.id)
        assertEquals(e.content, back.content)
        assertEquals(e.userText, back.userText)
        assertEquals(e.assistantText, back.assistantText)
        assertEquals(e.appContext, back.appContext)
        assertEquals(e.tags, back.tags)
        assertEquals(e.importance, back.importance)
        assertEquals(e.recordedAtUtc, back.recordedAtUtc)
    }

    @Test
    fun theEMBEDDINGcomesBackTheWayItWentIn() = runTest {
        // Bytes the other way round produce plausible nonsense rather than an
        // error, and a vector search silently returns the wrong neighbours.
        val s = SqliteEpisodicStore(conn())
        val e = episode()
        s.save(e)
        assertContentEquals(e.embedding, s.getRecent("u1", 10).single().embedding)
    }

    @Test
    fun theFloatCodecSurvivesTheAwkwardValues() {
        val hard = floatArrayOf(0f, -0f, 1f, -1f, Float.MIN_VALUE, Float.MAX_VALUE, 3.14159f)
        val back = SqliteEpisodicStore.bytesToFloats(SqliteEpisodicStore.floatsToBytes(hard))
        assertContentEquals(hard, back)
        assertEquals(0, SqliteEpisodicStore.bytesToFloats(null).size)
        assertEquals(0, SqliteEpisodicStore.bytesToFloats(ByteArray(2)).size)
    }

    @Test
    fun recentEpisodesAreNEWESTfirst() = runTest {
        val s = SqliteEpisodicStore(conn())
        s.save(episode("a", Instant.parse("2026-07-01T09:00:00Z")))
        s.save(episode("c", Instant.parse("2026-07-03T09:00:00Z")))
        s.save(episode("b", Instant.parse("2026-07-02T09:00:00Z")))
        assertEquals(listOf("c", "b", "a"), s.getRecent("u1", 10).map { it.id })
        assertEquals(2, s.getRecent("u1", 2).size)
    }

    @Test
    fun savingTheSameEpisodeTwiceKeepsONErow() = runTest {
        val s = SqliteEpisodicStore(conn())
        s.save(episode("a"))
        s.save(episode("a"))
        assertEquals(1, s.getRecent("u1", 10).size)
    }

    @Test
    fun deletingAnEpisodeRemovesIt() = runTest {
        val s = SqliteEpisodicStore(conn())
        s.save(episode("a"))
        s.delete("a")
        assertTrue(s.getRecent("u1", 10).isEmpty())
        s.delete("a")
    }

    @Test
    fun aGoalRoundTripsThroughSql() = runTest {
        val s = SqliteGoalStore(conn())
        val g = Goal(
            id = "g1", userId = "u1", title = "Ship the port", description = "all eight",
            status = GoalStatus.Active, priority = GoalPriority.High,
            createdUtc = Instant.parse("2026-07-01T09:00:00Z"),
            dueUtc = Instant.parse("2026-08-01T09:00:00Z"),
            notes = "one language at a time", progress = 0.6f,
        )
        s.upsertAsync(g)
        val back = s.getAsync("g1")!!
        assertEquals(g.title, back.title)
        assertEquals(g.status, back.status)
        assertEquals(g.priority, back.priority)
        assertEquals(g.createdUtc, back.createdUtc)
        assertEquals(g.dueUtc, back.dueUtc)
        assertEquals(g.notes, back.notes)
        assertEquals(0.6f, back.progress)
        assertNull(back.completedUtc)
    }

    @Test
    fun sqlGoalsFilterByUserAndByActive() = runTest {
        val s = SqliteGoalStore(conn())
        fun g(id: String, user: String, status: GoalStatus) = Goal(
            id = id, userId = user, title = "t", description = "d",
            status = status, priority = GoalPriority.Normal, createdUtc = Instant.EPOCH,
        )
        s.upsertAsync(g("a", "u1", GoalStatus.Active))
        s.upsertAsync(g("b", "u1", GoalStatus.Completed))
        s.upsertAsync(g("c", "u2", GoalStatus.Active))

        assertEquals(2, s.listAsync("u1").size)
        assertEquals(listOf("a"), s.getActiveAsync("u1").map { it.id })
    }

    @Test
    fun theSqlAndInMemoryGoalStoresAgree() = runTest {
        // Two implementations of one contract have to answer the same way, or a
        // test that passes in memory means nothing about the device.
        val g = Goal(
            id = "g1", userId = "u1", title = "t", description = "d",
            status = GoalStatus.Active, priority = GoalPriority.Low, createdUtc = Instant.EPOCH,
        )
        val a: IGoalStore = InMemoryGoalStore()
        val b: IGoalStore = SqliteGoalStore(conn())
        for (s in listOf(a, b)) {
            s.upsertAsync(g)
            assertEquals(1, s.listAsync("u1").size)
            assertEquals(1, s.getActiveAsync("u1").size)
            assertEquals("t", s.getAsync("g1")!!.title)
            s.deleteAsync("g1")
            assertNull(s.getAsync("g1"))
        }
    }
}
