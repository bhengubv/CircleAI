// PersonalHealthTest.kt
//
// Verifies the CircleAI.Personal.Health port against the C# reference:
//   - record + readSince (kind + since filter, ASC); latest returns newest
//   - addAllergy/allergies
//   - addMedication; endMedication (unknown throws); activeMedications excludes
//     ended, ordered by name ASC
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.personalhealth

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PersonalHealthTest {

    @Test
    fun `read since and latest`() {
        val b = InMemoryPersonalHealthBoard()
        val t1 = Instant.parse("2026-07-01T00:00:00Z")
        val t2 = Instant.parse("2026-07-05T00:00:00Z")
        val t3 = Instant.parse("2026-07-10T00:00:00Z")
        b.record(VitalReading(VitalKind.WeightKg, 80.0, t1, null))
        b.record(VitalReading(VitalKind.WeightKg, 79.0, t3, null))
        b.record(VitalReading(VitalKind.WeightKg, 79.5, t2, null))
        b.record(VitalReading(VitalKind.GlucoseMgDl, 95.0, t3, null))

        assertEquals(
            listOf(t2, t3),
            b.readSince(VitalKind.WeightKg, t2).map { it.atUtc },
        )
        assertEquals(79.0, b.latest(VitalKind.WeightKg)!!.value)
        assertNull(b.latest(VitalKind.OxygenPct))
    }

    @Test
    fun `allergies round trip`() {
        val b = InMemoryPersonalHealthBoard()
        b.addAllergy(Allergy("al1", "Penicillin", "Severe"))
        b.addAllergy(Allergy("al2", "Peanuts", "Moderate"))
        assertEquals(setOf("Penicillin", "Peanuts"), b.allergies.map { it.substance }.toSet())
    }

    @Test
    fun `medications active excludes ended, ordered by name`() {
        val b = InMemoryPersonalHealthBoard()
        val start = Instant.parse("2026-07-01T00:00:00Z")
        b.addMedication(Medication("m1", "Zoloft", "50mg", "OD", start, null))
        b.addMedication(Medication("m2", "Aspirin", "100mg", "OD", start, null))
        b.addMedication(Medication("m3", "Ibuprofen", "200mg", "PRN", start, null))
        b.endMedication("m3", Instant.parse("2026-07-08T00:00:00Z"))

        assertEquals(listOf("Aspirin", "Zoloft"), b.activeMedications().map { it.name })
        assertFailsWith<IllegalStateException> { b.endMedication("ghost", start) }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(PersonalHealthDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Personal.Health]"))
        assertTrue("Not_Medical_Advice" in PersonalHealthDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = PersonalHealthCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Personal.Health]"))
        a.explainHealthTermAsync("hypertension")
        assertTrue(fake.lastMessage!!.contains("Explain the medical term or concept in plain language: hypertension"))
    }
}
