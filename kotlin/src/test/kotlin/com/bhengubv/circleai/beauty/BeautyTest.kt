// BeautyTest.kt — verifies the CircleAI.Beauty port against the C# reference.

package com.bhengubv.circleai.beauty

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class BeautyTest {

    @Test
    fun `treatments appointments profiles and recommendations`() {
        val b = InMemoryBeautyBoard()
        b.addTreatment(Treatment("t1", "Hydrating Facial", 60, BigDecimal("450"), "ZAR"))
        b.addTreatment(Treatment("t2", "Acne Peel", 45, BigDecimal("600"), "ZAR"))
        assertEquals("Acne Peel", b.getTreatment("t2")!!.name)

        val d0 = Instant.parse("2026-03-01T09:00:00Z")
        b.book(Appointment("ap2", "Zed", "t1", d0.plusSeconds(3600), null))
        b.book(Appointment("ap1", "Amy", "t2", d0, "first visit"))
        b.book(Appointment("apX", "Out", "t1", d0.plusSeconds(999_999), null)) // out of range
        val between = b.appointmentsBetween(d0, d0.plusSeconds(7200))
        assertEquals(listOf("ap1", "ap2"), between.map { it.apptId }) // ASC, inclusive

        b.saveProfile(SkinProfile("Amy", "oily", listOf("Acne")))
        assertEquals(listOf("t2"), b.recommendFor("Amy").map { it.treatmentId }) // name contains concern
        assertTrue(b.recommendFor("Nobody").isEmpty())
    }

    @Test
    fun `domain context and adapter with overload`() = runTest {
        assertTrue(BeautyDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Beauty]"))
        assertTrue("POPIA" in BeautyDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = BeautyCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Beauty]"))
        a.assessIngredientCompatibilityAsync("retinol, AHA")
        assertTrue(fake.lastMessage!!.contains("Assess this ingredient list for layering safety"))
    }
}
