// AgricultureTest.kt — verifies the CircleAI.Agriculture port against the C# reference.

package com.bhengubv.circleai.agriculture

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class AgricultureTest {

    @Test
    fun `fields crops and average yield`() {
        val b = InMemoryFarmBoard()
        b.addField(Field("f1", 12.0, "loam", "drip"))
        assertEquals("loam", b.getField("f1")!!.soilType)

        b.plant(Crop("c1", "f1", "Maize", Instant.parse("2026-01-10T00:00:00Z"), null))
        b.plant(Crop("c0", "f1", "Maize", Instant.parse("2026-01-05T00:00:00Z"), null))
        b.plant(Crop("cx", "f2", "Wheat", Instant.parse("2026-01-01T00:00:00Z"), null))
        assertEquals(listOf("c0", "c1"), b.cropsForField("f1").map { it.cropId }) // ASC by plantedOn

        b.recordYield(YieldRecord("c0", 8.0, Instant.parse("2026-06-01T00:00:00Z")))
        b.recordYield(YieldRecord("c1", 10.0, Instant.parse("2026-06-02T00:00:00Z")))
        assertEquals(9.0, b.avgYieldOfVariety("maize"), 1e-9) // case-insensitive mean
        assertEquals(0.0, b.avgYieldOfVariety("rice"), 1e-9)  // none -> 0
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(AgricultureDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Agriculture]"))
        assertTrue("DAFF_regs" in AgricultureDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = AgricultureCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Agriculture]"))
        a.estimateYieldAsync("maize", 5.0, "irrigated")
        assertTrue(fake.lastMessage!!.contains("Estimate yield (t/ha and total tons) for 5.0ha of maize"))
    }
}
