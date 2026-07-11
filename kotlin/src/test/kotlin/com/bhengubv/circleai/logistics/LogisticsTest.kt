// LogisticsTest.kt
//
// Verifies the CircleAI.Logistics port against the C# reference:
//   - register shipment/vehicle (blank-id guards); Vehicles ordered by id
//   - planRoute throws on unknown vehicle; totalKm = Σ legs; cost = totalKm*costPerKm
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.logistics

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class LogisticsTest {

    @Test
    fun `register and plan route`() {
        val b = InMemoryLogisticsBoard()
        b.registerShipment(Shipment("sh1", "JHB", "CPT", 100.0, 2.0, "DAP", Instant.EPOCH))
        assertEquals("CPT", b.getShipment("sh1")!!.destination)

        b.registerVehicle(Vehicle("v2", 1000.0, 10.0, 12.5))
        b.registerVehicle(Vehicle("v1", 500.0, 5.0, 8.0))
        assertEquals(listOf("v1", "v2"), b.vehicles.map { it.vehicleId }) // ordered

        val plan = b.planRoute("v1", listOf(RouteLeg("A", "B", 100.0), RouteLeg("B", "C", 50.0)))
        assertEquals(150.0, plan.totalDistanceKm)
        assertTrue(BigDecimal("1200").compareTo(plan.estimatedCost) == 0) // 150 * 8.0
        assertEquals(2, plan.legs.size)
        assertTrue(plan.planId.startsWith("plan-"))

        assertFailsWith<IllegalStateException> { b.planRoute("ghost", emptyList()) }
        assertFailsWith<IllegalArgumentException> { b.registerVehicle(Vehicle(" ", 1.0, 1.0, 1.0)) }
        assertFailsWith<IllegalArgumentException> { b.registerShipment(Shipment("", "a", "b", 1.0, 1.0, "x", Instant.EPOCH)) }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(LogisticsDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Logistics]"))
        assertTrue("RTMS" in LogisticsDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = LogisticsCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Logistics]"))
        a.diagnoseDelayAsync("sh1 stuck at border", "customs hold")
        assertTrue(fake.lastMessage!!.contains("shipment delay"))
        a.optimiseRouteAsync("JHB", "CPT;DBN", "8h window")
        assertTrue(fake.lastMessage!!.contains("Optimise delivery routes from JHB"))
    }
}
