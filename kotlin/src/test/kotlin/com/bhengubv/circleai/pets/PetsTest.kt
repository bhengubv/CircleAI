// PetsTest.kt
//
// Verifies the CircleAI.Pets port against the C# reference:
//   - add/get; Pets ordered by name; vaccinationsFor newest-first; weightHistory
//     oldest-first; upcomingAppointments = future (UTC now), ordered ASC
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.pets

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class PetsTest {

    private val dob = Instant.parse("2020-07-01T00:00:00Z")
    private val past = Instant.parse("2020-01-01T00:00:00Z")

    @Test
    fun `pets vaccinations weights and appointments`() {
        val b = InMemoryPetsBoard()
        b.add(Pet("p2", "Zeus", "dog", "Boxer", dob))
        b.add(Pet("p1", "Ace", "cat", null, dob))
        assertEquals(listOf("Ace", "Zeus"), b.pets.map { it.name })
        assertEquals("Zeus", b.getPet("p2")!!.name)

        b.recordVaccination(Vaccination("p1", "Rabies", past, null))
        b.recordVaccination(Vaccination("p1", "FVRCP", past.plusSeconds(86400), null)) // newer
        assertEquals(listOf("FVRCP", "Rabies"), b.vaccinationsFor("p1").map { it.vaccine }) // newest-first

        b.recordWeight(WeightSample("p1", 4.0, past.plusSeconds(100)))
        b.recordWeight(WeightSample("p1", 3.0, past)) // earlier
        assertEquals(listOf(3.0, 4.0), b.weightHistory("p1").map { it.weightKg }) // oldest-first

        val future = Instant.now().plusSeconds(86400)
        b.schedule(VetAppointment("ap1", "p1", "checkup", future, "Dr Vet"))
        b.schedule(VetAppointment("ap2", "p1", "old", past, "Dr Vet")) // in the past -> excluded
        assertEquals(listOf("ap1"), b.upcomingAppointments().map { it.apptId })
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(PetsDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Pets]"))
        assertTrue("Vet_Referral_Required" in PetsDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = PetsCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Pets]"))
        a.triageSymptomAsync("dog", "Boxer", "limping")
        assertTrue(fake.lastMessage!!.contains("Triage this pet health concern"))
    }
}
