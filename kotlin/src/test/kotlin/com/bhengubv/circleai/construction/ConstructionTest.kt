// ConstructionTest.kt — verifies the CircleAI.Construction port against the C# reference.

package com.bhengubv.circleai.construction

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class ConstructionTest {

    @Test
    fun `tasks costs and budget`() {
        val b = InMemoryConstructionBoard()
        b.create(Project("pr1", "House", Instant.parse("2026-01-01T00:00:00Z"), null, BigDecimal("1000000"), "ZAR"))
        assertEquals("House", b.getProject("pr1")!!.name)

        b.add(ConstructionTask("t2", "pr1", "roof", Instant.parse("2026-03-01T00:00:00Z"), false))
        b.add(ConstructionTask("t1", "pr1", "foundation", Instant.parse("2026-02-01T00:00:00Z"), false))
        b.add(ConstructionTask("t3", "pr1", "done thing", Instant.parse("2026-02-15T00:00:00Z"), true))
        assertEquals(listOf("t1", "t2"), b.openConstructionTasksFor("pr1").map { it.constructionTaskId }) // ASC by due, open only
        b.complete("t1")
        assertEquals(listOf("t2"), b.openConstructionTasksFor("pr1").map { it.constructionTaskId })
        assertFailsWith<IllegalStateException> { b.complete("nope") }

        b.recordCost(CostEntry("c1", "pr1", "materials", BigDecimal("250000"), Instant.now()))
        b.recordCost(CostEntry("c2", "pr1", "labour", BigDecimal("150000"), Instant.now()))
        assertEquals(0, BigDecimal("400000").compareTo(b.spendFor("pr1")))
        assertEquals(0, BigDecimal("600000").compareTo(b.remainingBudget("pr1")))
        assertFailsWith<IllegalStateException> { b.remainingBudget("nope") }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(ConstructionDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Construction]"))
        assertTrue("OHS_Act" in ConstructionDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = ConstructionCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Construction]"))
        a.estimateCostAsync("brickwork", 120.0, "premium")
        assertTrue(fake.lastMessage!!.contains("Estimate cost for 120.0m² of brickwork, finish level premium"))
    }
}
