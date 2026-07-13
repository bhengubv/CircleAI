// CrmTest.kt
//
// Verifies the CircleAI.CRM port against the C# reference:
//   - contact store: upsert/get, blank-id guards, substring name/email search
//     (case-insensitive, ordered by name, topK cap)
//   - deal pipeline: upsert/get, blank-id guard, list-by-stage (case-insensitive,
//     ordered by value DESC)
//   - activity log: append per contact, blank-id guard, newest-first read + limit
//   - null implementations are fail-open no-ops with "null" backend

package com.bhengubv.circleai.crm

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CrmTest {

    @Test
    fun `contact store search is case-insensitive, ordered, capped`() = runTest {
        val store = InMemoryContactStore()
        assertEquals("in-memory", store.backendId)
        store.upsertAsync(Contact("c1", "Zoe Ndlovu", "zoe@example.com", null, null))
        store.upsertAsync(Contact("c2", "Ann Smith", "ann@work.co.za", null, null))
        store.upsertAsync(Contact("c3", "Bob Jones", null, "0821234567", null))

        assertEquals("Ann Smith", store.getAsync("c2")!!.fullName)
        assertNull(store.getAsync("missing"))

        // Match on name OR email substring (case-insensitive), ordered by name ASC.
        // "o" hits Zoe Ndlovu and Bob Jones by name, and Ann Smith by email
        // (ann@work.co.za contains "o"); OrdinalIgnoreCase order: Ann, Bob, Zoe.
        val byName = store.searchAsync("o")
        assertEquals(listOf("Ann Smith", "Bob Jones", "Zoe Ndlovu"), byName.map { it.fullName })

        // Match on email substring.
        val byEmail = store.searchAsync("WORK")
        assertEquals(listOf("Ann Smith"), byEmail.map { it.fullName })

        // topK cap.
        assertEquals(1, store.searchAsync("o", topK = 1).size)
    }

    @Test
    fun `contact store guards`() = runTest {
        val store = InMemoryContactStore()
        assertFailsWith<IllegalArgumentException> { store.upsertAsync(Contact(" ", "x", null, null, null)) }
        assertFailsWith<IllegalArgumentException> { store.getAsync("  ") }
        assertFailsWith<IllegalArgumentException> { store.searchAsync("q", topK = 0) }
    }

    @Test
    fun `deal pipeline lists by stage ordered by value desc`() = runTest {
        val pipe = InMemoryDealPipeline()
        pipe.upsertAsync(Deal("d1", "co", "Small", BigDecimal("100"), "ZAR", "Open"))
        pipe.upsertAsync(Deal("d2", "co", "Big", BigDecimal("900"), "ZAR", "open"))
        pipe.upsertAsync(Deal("d3", "co", "Won", BigDecimal("500"), "ZAR", "Closed"))

        assertEquals("Big", pipe.getAsync("d2")!!.name)
        val open = pipe.listByStageAsync("OPEN") // case-insensitive
        assertEquals(listOf("Big", "Small"), open.map { it.name }) // 900 before 100
        assertFailsWith<IllegalArgumentException> { pipe.upsertAsync(Deal("", "co", "x", BigDecimal.ONE, "ZAR", "Open")) }
        assertFailsWith<IllegalArgumentException> { pipe.listByStageAsync(" ") }
    }

    @Test
    fun `activity log newest first with limit`() = runTest {
        val log = InMemoryActivityLog()
        val t0 = Instant.parse("2026-07-01T00:00:00Z")
        log.appendAsync(Activity("a1", "c1", "call", "first", t0))
        log.appendAsync(Activity("a2", "c1", "email", "second", t0.plusSeconds(60)))
        log.appendAsync(Activity("a3", "c1", "note", "third", t0.plusSeconds(120)))
        log.appendAsync(Activity("a4", "c2", "call", "other", t0))

        val read = log.readForContactAsync("c1")
        assertEquals(listOf("third", "second", "first"), read.map { it.body })
        assertEquals(listOf("third", "second"), log.readForContactAsync("c1", limit = 2).map { it.body })
        assertTrue(log.readForContactAsync("nobody").isEmpty())
        assertFailsWith<IllegalArgumentException> { log.appendAsync(Activity("x", " ", "k", "b", t0)) }
    }

    @Test
    fun `null implementations are fail-open`() = runTest {
        assertEquals("null", NullContactStore.Instance.backendId)
        NullContactStore.Instance.upsertAsync(Contact("c", "n", null, null, null))
        assertNull(NullContactStore.Instance.getAsync("c"))
        assertTrue(NullContactStore.Instance.searchAsync("x").isEmpty())

        assertNull(NullDealPipeline.Instance.getAsync("d"))
        assertTrue(NullDealPipeline.Instance.listByStageAsync("Open").isEmpty())

        NullActivityLog.Instance.appendAsync(Activity("a", "c", "k", "b", Instant.EPOCH))
        assertTrue(NullActivityLog.Instance.readForContactAsync("c").isEmpty())
    }
}
