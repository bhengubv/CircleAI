// EnergyTest.kt — verifies the CircleAI.Energy port against the C# reference.

package com.bhengubv.circleai.energy

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class EnergyTest {

    private val since = Instant.parse("2026-01-01T00:00:00Z")

    @Test
    fun `readings totals cost and outages`() {
        val b = InMemoryEnergyBoard()
        b.record(MeterReading("m1", 100.0, since.plusSeconds(100)))
        b.record(MeterReading("m1", 130.0, since.plusSeconds(200)))
        b.record(MeterReading("m1", 5.0, since.minusSeconds(100))) // before window
        assertEquals(listOf(100.0, 130.0), b.readingsFor("m1", since).map { it.kwh }) // ASC
        assertEquals(30.0, b.totalKwhSince("m1", since), 1e-9) // last - first

        b.setTariff(EnergyTariff("tf1", "Home", 2.5, 1.0, "ZAR"))
        // 30 kWh * 2.5 = 75.
        assertEquals(0, BigDecimal.valueOf(75.0).compareTo(b.estimateCost("m1", "tf1", since)))
        assertFailsWith<IllegalStateException> { b.estimateCost("m1", "nope", since) }

        b.logOutage(Outage("o1", "Zone A", since, null, "storm"))
        b.logOutage(Outage("o2", "Zone B", since, since.plusSeconds(60), "fixed"))
        assertEquals(listOf("o1"), b.activeOutages().map { it.outageId }) // no end
    }

    @Test
    fun `total kwh is zero with fewer than two readings`() {
        val b = InMemoryEnergyBoard()
        b.record(MeterReading("m9", 50.0, since.plusSeconds(10)))
        assertEquals(0.0, b.totalKwhSince("m9", since), 1e-9)
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(EnergyDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Energy]"))
        assertTrue("NERSA" in EnergyDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = EnergyCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Energy]"))
        a.draftLoadSheddingPlanAsync("4", "fridge, router")
        assertTrue(fake.lastMessage!!.contains("Draft a load-shedding plan for 4-person home, critical: fridge, router"))
    }
}
