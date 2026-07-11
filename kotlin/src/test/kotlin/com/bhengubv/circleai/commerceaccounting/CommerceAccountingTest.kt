// CommerceAccountingTest.kt
//
// Verifies the CircleAI.Commerce.Accounting port against the C# reference:
//   - post rejects negative debit/credit
//   - accountBalance / sum compute Σ(debit − credit); sum + forAccount filter
//     by year+month; forAccount orders by atUtc ASC
//   - defineTax/getTax; netProfit = revenue − expense for a period
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.commerceaccounting

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.LocalDateTime
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CommerceAccountingTest {

    private fun money(s: String) = BigDecimal(s)
    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private fun entry(id: String, at: LocalDateTime, acct: String, dr: String, cr: String) =
        AccountingEntry(id, at, acct, money(dr), money(cr), "memo")

    private val jul = LocalDateTime.of(2026, 7, 10, 12, 0)
    private val aug = LocalDateTime.of(2026, 8, 10, 12, 0)

    @Test
    fun `post rejects negatives`() {
        val b = InMemoryAccountingBoard()
        assertFailsWith<IllegalArgumentException> {
            b.post(entry("e", jul, "4000", "-1", "0"))
        }
        assertFailsWith<IllegalArgumentException> {
            b.post(entry("e", jul, "4000", "0", "-1"))
        }
    }

    @Test
    fun `balance sum forAccount and period filtering`() {
        val b = InMemoryAccountingBoard()
        b.post(entry("e1", jul, "4000", "100", "0"))
        b.post(entry("e2", jul, "4000", "0", "30"))
        b.post(entry("e3", aug, "4000", "5", "0"))
        // whole-account balance across periods: (100-0)+(0-30)+(5-0) = 75
        assertMoney("75", b.accountBalance("4000"))
        // July only: (100) + (-30) = 70
        assertMoney("70", b.sum("4000", Period(2026, 7)))
        assertEquals(listOf("e1", "e2"), b.forAccount("4000", Period(2026, 7)).map { it.entryId })
        assertTrue(b.forAccount("9999", Period(2026, 7)).isEmpty())
    }

    @Test
    fun `tax rates and net profit`() {
        val b = InMemoryAccountingBoard()
        assertNull(b.getTax("V15"))
        b.defineTax(TaxRate("V15", 15.0))
        assertEquals(15.0, b.getTax("V15")!!.percentage)

        b.post(entry("rev", jul, "4000", "0", "200")) // revenue credit -> sum = -200
        b.post(entry("exp", jul, "5000", "80", "0")) // expense debit -> sum = 80
        // netProfit = sum(rev) - sum(exp) = -200 - 80 = -280
        assertMoney("-280", b.netProfit(Period(2026, 7), "4000", "5000"))
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CommerceAccountingDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Commerce.Accounting]"))
        assertTrue("IFRS" in CommerceAccountingDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CommerceAccountingCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Commerce.Accounting]"))
        a.explainJournalEntryAsync("paid rent")
        assertTrue(fake.lastMessage!!.contains("double-entry journal lines: paid rent"))
    }
}
