// SportsTest.kt — verifies the CircleAI.Sports port against the C# reference.

package com.bhengubv.circleai.sports

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SportsTest {

    private val t0 = Instant.parse("2026-03-04T10:00:00Z") // a Wednesday

    @Test
    fun `history is newest-first and capped`() {
        val b = InMemorySportsBoard()
        b.log(Activity("a1", "u1", DistanceKind.Run, 5.0, Duration.ofMinutes(30), t0))
        b.log(Activity("a2", "u1", DistanceKind.Run, 6.0, Duration.ofMinutes(35), t0.plusSeconds(60)))
        b.log(Activity("a3", "u2", DistanceKind.Run, 9.0, Duration.ofMinutes(40), t0))
        assertEquals(listOf("a2", "a1"), b.history("u1").map { it.activityId })
        assertEquals(listOf("a2"), b.history("u1", 1).map { it.activityId })
        assertFailsWith<IllegalArgumentException> { b.history("u1", 0) }
    }

    @Test
    fun `total km this week sums from week start and best picks fastest`() {
        val b = InMemorySportsBoard()
        // Sunday 2026-03-01 is the week start for a Wed 2026-03-04.
        b.log(Activity("a1", "u1", DistanceKind.Run, 5.0, Duration.ofMinutes(30), Instant.parse("2026-03-02T08:00:00Z")))
        b.log(Activity("a2", "u1", DistanceKind.Run, 3.0, Duration.ofMinutes(20), Instant.parse("2026-03-04T08:00:00Z")))
        // Before the week start -> excluded from the weekly sum. Also kept below the
        // 10km best() floor (like the Rust/C sibling ports' "too short" decoy) so a
        // faster-but-shorter effort never wins Best over the qualifying 10km runs.
        b.log(Activity("aOld", "u1", DistanceKind.Run, 4.0, Duration.ofMinutes(10), Instant.parse("2026-02-20T08:00:00Z")))
        assertEquals(8.0, b.totalKmThisWeek("u1", DistanceKind.Run, t0), 1e-9)

        b.log(Activity("fast", "u1", DistanceKind.Run, 10.0, Duration.ofMinutes(45), t0))
        b.log(Activity("faster", "u1", DistanceKind.Run, 10.0, Duration.ofMinutes(42), t0))
        // Best over >=10km picks the fastest qualifying effort: faster (42) beats fast (45);
        // the sub-10km runs (a1/a2/aOld) do not qualify even though aOld is quicker.
        val best = b.best("u1", DistanceKind.Run, 10.0)!!
        assertEquals(Duration.ofMinutes(42), best.time)
        assertNull(b.best("u1", DistanceKind.Swim, 1.0))
    }

    @Test
    fun `schedule complete and upcoming`() {
        val b = InMemorySportsBoard()
        val future = Instant.now().plusSeconds(86_400)
        b.schedule(TrainingSession("s1", "u1", "intervals", future, false))
        b.schedule(TrainingSession("s2", "u1", "long run", Instant.now().minusSeconds(3600), false)) // past -> excluded
        assertEquals(listOf("s1"), b.upcoming("u1").map { it.sessionId })
        b.complete("s1")
        assertTrue(b.upcoming("u1").isEmpty()) // completed -> excluded
        assertFailsWith<IllegalStateException> { b.complete("nope") }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(SportsDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Sports]"))
        assertTrue("WADA" in SportsDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = SportsCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Sports]"))
        a.designTrainingProgramAsync("running", "amateur", "sub-40 10k", 8)
        assertTrue(fake.lastMessage!!.contains("8-week periodised training programme"))
        a.planRecoveryAsync("hard", "2")
        assertTrue(fake.lastMessage!!.contains("Plan recovery between sessions"))
    }
}
