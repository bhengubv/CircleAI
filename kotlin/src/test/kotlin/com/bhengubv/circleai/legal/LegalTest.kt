// LegalTest.kt
//
// Verifies the CircleAI.Legal port against the C# reference semantics:
//   - open/close (unknown matter throws); activeMatters filters Open and
//     orders by openedAtUtc DESC
//   - contractsExpiringBefore keeps set-and-<=cutoff, ordered ASC (null expiry
//     excluded)
//   - upcomingDeadlines keeps DueOn>=now ordered ASC
//   - clausesByTag rejects blank, matches case-insensitively
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.legal

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.LocalDate
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class LegalTest {

    private fun matter(id: String, opened: Instant, open: Boolean = true) =
        Matter(id, "Title $id", "ZA", "ClientCo", opened, open)

    @Test
    fun `open close and active ordering`() {
        val b = InMemoryLegalBoard()
        b.open(matter("m1", Instant.parse("2026-07-01T00:00:00Z")))
        b.open(matter("m2", Instant.parse("2026-07-05T00:00:00Z")))
        b.open(matter("m3", Instant.parse("2026-07-03T00:00:00Z")))
        b.close("m3")
        assertEquals(listOf("m2", "m1"), b.activeMatters.map { it.matterId })
        assertNull(b.getMatter("nope"))
        assertFailsWith<IllegalStateException> { b.close("ghost") }
    }

    @Test
    fun `contracts expiring before cutoff, nulls excluded, ordered ascending`() {
        val b = InMemoryLegalBoard()
        b.addContract(Contract("c1", "m1", "A", LocalDate.of(2026, 1, 1), LocalDate.of(2026, 12, 31), listOf("X")))
        b.addContract(Contract("c2", "m1", "B", LocalDate.of(2026, 1, 1), LocalDate.of(2026, 6, 30), listOf("Y")))
        b.addContract(Contract("c3", "m1", "C", LocalDate.of(2026, 1, 1), null, listOf("Z")))
        val hits = b.contractsExpiringBefore(LocalDate.of(2026, 12, 31))
        assertEquals(listOf("c2", "c1"), hits.map { it.contractId })
    }

    @Test
    fun `upcoming deadlines from now, ascending`() {
        val b = InMemoryLegalBoard()
        b.add(LegalDeadline("d1", "m1", "file", LocalDate.of(2026, 7, 1)))
        b.add(LegalDeadline("d2", "m1", "serve", LocalDate.of(2026, 8, 1)))
        b.add(LegalDeadline("d3", "m1", "past", LocalDate.of(2026, 6, 1)))
        val up = b.upcomingDeadlines(LocalDate.of(2026, 7, 1))
        assertEquals(listOf("d1", "d2"), up.map { it.deadlineId })
    }

    @Test
    fun `clauses by tag is case-insensitive and rejects blank`() {
        val b = InMemoryLegalBoard()
        b.addClause(Clause("cl1", "Indemnity", "body", listOf("Risk", "Liability")))
        b.addClause(Clause("cl2", "Term", "body", listOf("Duration")))
        assertEquals(listOf("cl1"), b.clausesByTag("risk").map { it.clauseId })
        assertTrue(b.clausesByTag("none").isEmpty())
        assertFailsWith<IllegalArgumentException> { b.clausesByTag("  ") }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(LegalDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Legal]"))
        assertTrue("POPIA" in LegalDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = LegalCompanionAdapter(fake)
        a.streamAsync("q")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Legal]"))
        a.draftClauseAsync("indemnity", "buyer", "ZA")
        assertTrue(fake.lastMessage!!.contains("indemnity clause favouring the buyer in ZA"))
    }
}
