// HealthcareTest.kt
//
// Verifies the CircleAI.Healthcare port against the C# reference semantics:
//   - register/getPatient round-trips; unknown patient is null
//   - schedule + updateStatus (unknown appointment throws); appointmentsFor
//     orders by atUtc ASC
//   - prescribe + prescriptionsFor orders by prescribedUtc DESC
//   - domain-context constants; adapter enriches ordinary turns and forwards
//     domain-helper prompts verbatim to agentAsync

package com.bhengubv.circleai.healthcare

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.LocalDate
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class HealthcareTest {

    private fun appt(id: String, patient: String, at: Instant, status: String = "booked") =
        HealthAppointment(id, patient, "Dr. Who", at, status)

    @Test
    fun `register and get patient, unknown is null`() {
        val b = InMemoryHealthcareBoard()
        val p = Patient("p1", "Ada", LocalDate.of(1990, 1, 2))
        b.register(p)
        assertEquals(p, b.getPatient("p1"))
        assertNull(b.getPatient("nope"))
    }

    @Test
    fun `appointments ordered ascending and status update, unknown throws`() {
        val b = InMemoryHealthcareBoard()
        val t1 = Instant.parse("2026-07-10T09:00:00Z")
        val t2 = Instant.parse("2026-07-11T09:00:00Z")
        b.schedule(appt("a2", "p1", t2))
        b.schedule(appt("a1", "p1", t1))
        b.schedule(appt("aX", "p2", t1))
        assertEquals(listOf("a1", "a2"), b.appointmentsFor("p1").map { it.apptId })

        b.updateStatus("a1", "completed")
        assertEquals("completed", b.appointmentsFor("p1").first { it.apptId == "a1" }.status)
        assertFailsWith<IllegalStateException> { b.updateStatus("ghost", "x") }
    }

    @Test
    fun `prescriptions ordered by prescribed date descending`() {
        val b = InMemoryHealthcareBoard()
        b.prescribe(Prescription("r1", "p1", "Amox", "500mg", "TDS", Instant.parse("2026-07-01T00:00:00Z")))
        b.prescribe(Prescription("r2", "p1", "Ibu", "200mg", "PRN", Instant.parse("2026-07-05T00:00:00Z")))
        assertEquals(listOf("r2", "r1"), b.prescriptionsFor("p1").map { it.rxId })
        assertTrue(b.prescriptionsFor("other").isEmpty())
    }

    @Test
    fun `domain context constants`() {
        assertTrue(HealthcareDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Healthcare]"))
        assertTrue("ICD10" in HealthcareDomainContext.complianceFlags)
        assertEquals(4, HealthcareDomainContext.suggestedTools.size)
    }

    @Test
    fun `adapter enriches turns and forwards domain helpers`() = runTest {
        val fake = FakeCompanionSession()
        val a = HealthcareCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Healthcare]"))
        assertTrue(fake.lastMessage!!.endsWith("\n\nhi"))

        a.suggestIcd10CodesAsync("type 2 diabetes")
        assertTrue(fake.lastMessage!!.contains("ICD-10-CM codes"))
        assertTrue(fake.lastMessage!!.contains("type 2 diabetes"))
        assertEquals(fake.identityId, a.identityId)
    }
}
