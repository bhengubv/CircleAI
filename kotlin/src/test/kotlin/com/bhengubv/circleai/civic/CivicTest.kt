// CivicTest.kt — verifies the CircleAI.Civic port against the C# reference.

package com.bhengubv.circleai.civic

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class CivicTest {

    @Test
    fun `issues reps and events`() {
        val b = InMemoryCivicBoard()
        b.report(CivicIssue("i1", "water", "burst pipe", -26.2, 28.0, Instant.now(), "Open"))
        b.report(CivicIssue("i2", "roads", "pothole", -26.2, 28.0, Instant.now(), "Open"))
        b.resolve("i2", "Resolved")
        assertEquals(listOf("i1"), b.openIssues().map { it.issueId }) // resolved excluded
        assertFailsWith<IllegalStateException> { b.resolve("nope", "Resolved") }

        b.addRep(Representative("r1", "MP A", "Ward 1", "a@x.gov", "Ward1"))
        b.addRep(Representative("r2", "MP B", "Ward 2", "b@x.gov", "Ward2"))
        assertEquals(listOf("r1"), b.repsForDistrict("ward1").map { it.repId }) // case-insensitive

        val future = Instant.now().plusSeconds(86_400)
        b.schedule(CivicEvent("e1", "Town hall", future, "Hall", "public"))
        b.schedule(CivicEvent("e2", "Past", Instant.now().minusSeconds(3600), "Hall", "public"))
        assertEquals(listOf("e1"), b.upcomingEvents().map { it.eventId }) // future ASC
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CivicDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Civic]"))
        assertTrue("PAJA" in CivicDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CivicCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Civic]"))
        a.prepareCouncilQuestionsAsync("budget", 5)
        assertTrue(fake.lastMessage!!.contains("Prepare 5 pointed questions for council on budget"))
    }
}
