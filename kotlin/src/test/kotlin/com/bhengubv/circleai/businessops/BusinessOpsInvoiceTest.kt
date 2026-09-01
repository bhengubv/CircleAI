package com.bhengubv.circleai.businessops

import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Invoice arithmetic, overdue rules, numbering and recurrence. */
class BusinessOpsInvoiceTest {

    private fun zar(v: String) = Money.of(BigDecimal(v), "ZAR")!!

    private val vatLines = listOf(
        BusinessInvoiceLine("Logo suite", BigDecimal.ONE, zar("8500"), BigDecimal("0.15")),
        BusinessInvoiceLine("Business cards", BigDecimal("2"), zar("750"), BigDecimal("0.15")),
    )

    private fun invoice(
        lines: List<BusinessInvoiceLine> = vatLines,
        status: InvoiceStatus = InvoiceStatus.DRAFT,
        due: LocalDate? = null,
        paid: Money? = null,
    ) = BusinessInvoice("i", clientId = "c", currency = "ZAR", lines = lines,
        status = status, dueDate = due, amountPaid = paid)

    @Test fun `a line totals quantity times price plus tax`() {
        val l = BusinessInvoiceLine("x", BigDecimal("2"), zar("750"), BigDecimal("0.15"))
        assertEquals(0, l.lineSubtotal.amount.compareTo(BigDecimal("1500")))
        assertEquals(0, l.lineTax.amount.compareTo(BigDecimal("225")))
        assertEquals(0, l.lineTotal.amount.compareTo(BigDecimal("1725")))
    }

    @Test fun `the invoice sums its lines`() {
        val i = invoice()
        assertEquals(0, i.subtotal.amount.compareTo(BigDecimal("10000")))
        assertEquals(0, i.taxTotal.amount.compareTo(BigDecimal("1500")))
        assertEquals(0, i.total.amount.compareTo(BigDecimal("11500")))
    }

    // Rounding at the LINE, then summing - so the total matches what the
    // customer gets adding the printed lines by hand.
    @Test fun `each line rounds before the lines are summed`() {
        val thirds = (1..3).map {
            BusinessInvoiceLine("third", BigDecimal.ONE, zar("0.335"), BigDecimal.ZERO)
        }
        // 0.335 rounds to 0.34 per line, so 1.02 - not 1.005 rounded to 1.01.
        assertEquals(0, invoice(thirds).subtotal.amount.compareTo(BigDecimal("1.02")))
    }

    @Test fun `an empty invoice totals zero in its own currency`() {
        val i = BusinessInvoice("i", clientId = "c", currency = "NGN")
        assertTrue(i.total.isZero)
        assertEquals("NGN", i.total.currency)
        assertTrue(i.isSettled)
    }

    @Test fun `balance due subtracts what was paid`() {
        val i = invoice(paid = zar("1500"))
        assertEquals(0, i.balanceDue.amount.compareTo(BigDecimal("10000")))
        assertFalse(i.isSettled)
    }

    @Test fun `an unpaid sent invoice past its due date is overdue`() {
        val i = invoice(status = InvoiceStatus.SENT, due = LocalDate.of(2026, 7, 31))
        assertTrue(i.isOverdue(LocalDate.of(2026, 8, 1)))
        assertFalse(i.isOverdue(LocalDate.of(2026, 7, 31)), "the due date itself is not late")
    }

    // A draft was never sent, so it cannot be late.
    @Test fun `a draft is never overdue`() {
        val i = invoice(status = InvoiceStatus.DRAFT, due = LocalDate.of(2026, 7, 31))
        assertFalse(i.isOverdue(LocalDate.of(2027, 1, 1)))
    }

    @Test fun `a cancelled invoice is never overdue`() {
        val i = invoice(status = InvoiceStatus.CANCELLED, due = LocalDate.of(2026, 7, 31))
        assertFalse(i.isOverdue(LocalDate.of(2027, 1, 1)))
    }

    @Test fun `a settled invoice is never overdue`() {
        val i = invoice(status = InvoiceStatus.SENT, due = LocalDate.of(2026, 7, 31),
            paid = zar("11500"))
        assertFalse(i.isOverdue(LocalDate.of(2027, 1, 1)))
    }

    // Sequential and gapless per year - an auditor asks about gaps.
    @Test fun `numbers are sequential and zero padded`() {
        val g = SequentialInvoiceNumberGenerator(year = 2026)
        assertEquals("INV-2026-0001", g.next())
        assertEquals("INV-2026-0002", g.next())
        assertEquals("AC/2027-0042", SequentialInvoiceNumberGenerator("AC/", 2027, 41).next())
    }

    // ── Recurrence ──────────────────────────────────────────────────────────

    @Test fun `a one-off has no next occurrence`() {
        assertEquals(null, RecurrenceRule.ONCE.next(Instant.EPOCH))
        assertFalse(RecurrenceRule.ONCE.isRecurring)
    }

    @Test fun `daily and weekly step by days`() {
        val now = Instant.EPOCH
        assertEquals(now.plusSeconds(86_400), RecurrenceRule(Recurrence.DAILY).next(now))
        assertEquals(now.plusSeconds(7 * 86_400), RecurrenceRule(Recurrence.WEEKLY).next(now))
        assertEquals(now.plusSeconds(3 * 86_400), RecurrenceRule(Recurrence.DAILY, 3).next(now))
    }

    // MONTHLY steps by CALENDAR month, so the 31st stays the last day of the
    // month instead of drifting backwards every cycle.
    @Test fun `monthly steps by calendar month not thirty days`() {
        val jan31 = LocalDate.of(2028, 1, 31).atStartOfDay().toInstant(java.time.ZoneOffset.UTC)
        val next = RecurrenceRule(Recurrence.MONTHLY).next(jan31)!!
        val d = java.time.LocalDate.ofInstant(next, java.time.ZoneOffset.UTC)
        assertEquals(LocalDate.of(2028, 2, 29), d, "a leap February clamps to the 29th")
    }

    @Test fun `a zero interval is treated as one`() {
        val now = Instant.EPOCH
        assertEquals(now.plusSeconds(86_400), RecurrenceRule(Recurrence.DAILY, 0).next(now))
    }

    @Test fun `a reminder is due once its time has passed and it is not done`() {
        val r = Reminder("r1", "call", Instant.EPOCH.plusSeconds(100))
        assertFalse(r.isDue(Instant.EPOCH))
        assertTrue(r.isDue(Instant.EPOCH.plusSeconds(100)), "the due instant itself counts")
        assertTrue(r.isDue(Instant.EPOCH.plusSeconds(200)))
        assertFalse(r.copy(completed = true).isDue(Instant.EPOCH.plusSeconds(200)))
    }
}
