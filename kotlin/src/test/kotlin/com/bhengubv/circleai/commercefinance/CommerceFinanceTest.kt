// CommerceFinanceTest.kt
//
// Verifies the CircleAI.Commerce.Finance port against the C# reference:
//   - issue/get; recordPayment; remainingOn = Σ(amount×(1+tax%)) − payments
//   - markOverdue flips due<asOf & not Paid to "Overdue"; overdue() returns them
//   - totalOutstanding sums remaining across invoices
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.commercefinance

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CommerceFinanceTest {

    private fun money(s: String) = BigDecimal(s)
    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private fun invoice(id: String, due: LocalDate, status: String = "Sent", lines: List<InvoiceLine>) =
        Invoice(id, "cust", LocalDate.of(2026, 7, 1), due, lines, "ZAR", status)

    @Test
    fun `remaining on nets billed against payments`() {
        val b = InMemoryInvoiceBoard()
        b.issue(
            invoice(
                "inv1", LocalDate.of(2026, 8, 1),
                lines = listOf(InvoiceLine("work", money("100"), 15.0), InvoiceLine("parts", money("50"), 0.0)),
            ),
        )
        // billed = 100*1.15 + 50*1.0 = 165
        assertMoney("165", b.remainingOn("inv1"))
        b.recordPayment(FinancePayment("p1", "inv1", money("65"), Instant.EPOCH))
        assertMoney("100", b.remainingOn("inv1"))
        // unknown invoice -> 0
        assertMoney("0", b.remainingOn("nope"))
    }

    @Test
    fun `mark overdue and overdue listing`() {
        val b = InMemoryInvoiceBoard()
        b.issue(invoice("past", LocalDate.of(2026, 6, 1), lines = listOf(InvoiceLine("x", money("10"), 0.0))))
        b.issue(invoice("future", LocalDate.of(2026, 9, 1), lines = listOf(InvoiceLine("x", money("10"), 0.0))))
        b.issue(invoice("paid", LocalDate.of(2026, 6, 1), status = "Paid", lines = listOf(InvoiceLine("x", money("10"), 0.0))))

        b.markOverdue(LocalDate.of(2026, 7, 1))
        assertEquals(listOf("past"), b.overdue().map { it.invoiceId })
        // "paid" must not be flipped even though due<asOf
        assertTrue(b.overdue().none { it.invoiceId == "paid" })
    }

    @Test
    fun `total outstanding sums remaining`() {
        val b = InMemoryInvoiceBoard()
        b.issue(invoice("a", LocalDate.of(2026, 8, 1), lines = listOf(InvoiceLine("x", money("100"), 0.0))))
        b.issue(invoice("b", LocalDate.of(2026, 8, 1), lines = listOf(InvoiceLine("y", money("40"), 0.0))))
        b.recordPayment(FinancePayment("p", "a", money("30"), Instant.EPOCH))
        // (100-30) + 40 = 110
        assertMoney("110", b.totalOutstanding())
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CommerceFinanceDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Commerce.Finance]"))
        assertTrue("NCA_34_2005" in CommerceFinanceDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CommerceFinanceCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Commerce.Finance]"))
        a.forecastCashFlowAsync("data", 8)
        assertTrue(fake.lastMessage!!.contains("Forecast cash flow for 8 weeks"))
    }
}
