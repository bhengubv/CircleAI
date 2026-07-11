// HrTest.kt
//
// Verifies the CircleAI.HR port against the C# reference:
//   - hire/get; Employees ordered by name; decideLeave throws on unknown;
//     pendingLeaves filters Status == Pending (case-insensitive)
//   - avgRatingFor averages ratings, 0.0 when none
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.hr

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class HrTest {

    private val hiredOn = Instant.parse("2020-01-01T00:00:00Z")
    private fun emp(id: String, name: String) = Employee(id, name, "Engineer", hiredOn, BigDecimal("50000"), "ZAR")

    @Test
    fun `hire, ordering, leave decisions`() {
        val b = InMemoryHRBoard()
        b.hire(emp("e2", "Zoe"))
        b.hire(emp("e1", "Ann"))
        assertEquals("Zoe", b.getEmployee("e2")!!.name)
        assertEquals(listOf("Ann", "Zoe"), b.employees.map { it.name })

        b.request(LeaveRequest("l1", "e1", "Annual", hiredOn, hiredOn, "Pending"))
        b.request(LeaveRequest("l2", "e2", "Sick", hiredOn, hiredOn, "Approved"))
        assertEquals(listOf("l1"), b.pendingLeaves().map { it.requestId })

        b.decideLeave("l1", "Approved")
        assertTrue(b.pendingLeaves().isEmpty())
        assertFailsWith<IllegalStateException> { b.decideLeave("nope", "Approved") }
    }

    @Test
    fun `avg rating averages or zero`() {
        val b = InMemoryHRBoard()
        assertEquals(0.0, b.avgRatingFor("e1"))
        b.review(PerformanceReview("r1", "e1", hiredOn, 4, "good"))
        b.review(PerformanceReview("r2", "e1", hiredOn, 2, "meh"))
        b.review(PerformanceReview("r3", "e2", hiredOn, 5, "great"))
        assertEquals(3.0, b.avgRatingFor("e1"))
        assertEquals(5.0, b.avgRatingFor("e2"))
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(HRDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: HR]"))
        assertTrue("BCEA" in HRDomainContext.complianceFlags)
        assertTrue("hris" in HRDomainContext.suggestedTools)

        val fake = FakeCompanionSession()
        val a = HRCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: HR]"))
        a.adviseOnDisciplinaryAsync("lateness", "clean record")
        assertTrue(fake.lastMessage!!.contains("progressive discipline"))
        a.structureInterviewLoopAsync("SRE", 4)
        assertTrue(fake.lastMessage!!.contains("interview loop for SRE in 4 hours"))
    }
}
