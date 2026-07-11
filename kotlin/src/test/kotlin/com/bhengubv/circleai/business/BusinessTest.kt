// BusinessTest.kt
//
// Verifies the CircleAI.Business port against the C# reference:
//   - add/get; childrenOf by parent; latestKpi newest value / NaN;
//     setTarget keying + targetAchievement = latest/target, NaN when missing/zero
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.business

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class BusinessTest {

    private val t0 = Instant.parse("2026-01-01T00:00:00Z")

    @Test
    fun `hierarchy, latest kpi, target achievement`() {
        val b = InMemoryBusinessBoard()
        b.add(BusinessUnit("root", "Root", "", listOf("rev")))
        b.add(BusinessUnit("sales", "Sales", "root", listOf("rev")))
        b.add(BusinessUnit("eng", "Eng", "root", listOf("velocity")))

        assertEquals("Root", b.getUnit("root")!!.name)
        assertEquals(setOf("Sales", "Eng"), b.childrenOf("root").map { it.name }.toSet())

        assertTrue(b.latestKpi("sales", "revenue").isNaN()) // none yet
        b.record(KpiSample("sales", "revenue", 100.0, t0))
        b.record(KpiSample("sales", "revenue", 250.0, t0.plusSeconds(3600))) // newer
        assertEquals(250.0, b.latestKpi("sales", "revenue"))

        b.setTarget(QuarterTarget("sales", "revenue", 2026, 1, 500.0))
        assertEquals(0.5, b.targetAchievement("sales", "revenue", 2026, 1)) // 250/500
        assertTrue(b.targetAchievement("sales", "revenue", 2026, 2).isNaN()) // missing target
        b.setTarget(QuarterTarget("sales", "revenue", 2026, 3, 0.0))
        assertTrue(b.targetAchievement("sales", "revenue", 2026, 3).isNaN()) // zero target
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(BusinessDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Business]"))
        assertTrue("POPIA" in BusinessDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = BusinessCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Business]"))
        a.analyseUnitEconomicsAsync("Widget", BigDecimal("1000"), BigDecimal("400"), BigDecimal("100"))
        assertTrue(fake.lastMessage!!.contains("unit economics for Widget"))
        a.suggestExperimentAsync("activation", 10.0, 20.0)
        assertTrue(fake.lastMessage!!.contains("move activation from 10.0 to 20.0"))
    }
}
