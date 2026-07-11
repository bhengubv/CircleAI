// PersonalFinanceTest.kt
//
// Verifies the CircleAI.Personal.Finance port against the C# reference:
//   - upsert/getAccount; record (unknown account throws) mutates balance
//   - listForMonth filters by account + UTC year/month
//   - setBudget is case-insensitive on category; budgets ordered by category ASC
//   - summarise: byCategory signed sums, totalIn = Σ positive, totalOut = −Σ negative
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.personalfinance

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PersonalFinanceTest {

    private fun money(s: String) = BigDecimal(s)
    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private fun tx(id: String, amt: String, cat: String, at: Instant) =
        FinanceTransaction(id, "a1", money(amt), cat, null, at)

    private val jul = Instant.parse("2026-07-15T00:00:00Z")

    @Test
    fun `record requires known account and mutates balance`() {
        val b = InMemoryPersonalFinanceBoard()
        b.upsert(Account("a1", "Cheque", money("100"), "ZAR"))
        assertEquals("Cheque", b.getAccount("a1")!!.name)
        assertNull(b.getAccount("x"))

        b.record(tx("t1", "-40", "food", jul))
        assertMoney("60", b.getAccount("a1")!!.balance)
        assertFailsWith<IllegalStateException> { b.record(tx("bad", "1", "x", jul).copy(accountId = "ghost")) }
    }

    @Test
    fun `budgets case-insensitive and ordered`() {
        val b = InMemoryPersonalFinanceBoard()
        b.setBudget(BudgetLine("Food", money("2000")))
        b.setBudget(BudgetLine("food", money("2500"))) // overwrites (case-insensitive key)
        b.setBudget(BudgetLine("Airtime", money("300")))
        val budgets = b.budgets
        assertEquals(listOf("Airtime", "food"), budgets.map { it.category })
        assertMoney("2500", budgets.first { it.category.equals("food", ignoreCase = true) }.monthlyLimit)
    }

    @Test
    fun `summarise splits in and out and groups by category`() {
        val b = InMemoryPersonalFinanceBoard()
        b.upsert(Account("a1", "Cheque", money("0"), "ZAR"))
        b.record(tx("t1", "5000", "salary", jul))
        b.record(tx("t2", "-1200", "rent", jul))
        b.record(tx("t3", "-300", "food", jul))
        b.record(tx("t4", "-200", "food", jul))
        // a transaction in a different month must be excluded
        b.record(tx("t5", "-999", "food", Instant.parse("2026-06-15T00:00:00Z")))

        val s = b.summarise("a1", 2026, 7)
        assertEquals(2026, s.year)
        assertEquals(7, s.month)
        assertMoney("5000", s.totalIn)
        assertMoney("1700", s.totalOut) // 1200 + 300 + 200
        assertMoney("-500", s.byCategory.getValue("food")) // -300 + -200
        assertMoney("5000", s.byCategory.getValue("salary"))
        assertEquals(2, b.listForMonth("a1", 2026, 7).count { it.category == "food" })
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(PersonalFinanceDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Personal.Finance]"))
        assertTrue("Not_Financial_Advice" in PersonalFinanceDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = PersonalFinanceCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Personal.Finance]"))
        a.createDebtPlanAsync("card R5000 @ 22%")
        assertTrue(fake.lastMessage!!.contains("avalanche method"))
    }
}
