package com.bhengubv.circleai.documents

import java.math.BigDecimal
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The invoice arithmetic, which is the only part of this module that can be
 * wrong by a cent and still look right.
 */
class InvoiceDocumentTest {

    private val from = InvoiceParty("Mokoena Logistics", taxNumber = "4123456789")
    private val to = InvoiceParty("Shoprite Checkers", address = "Brackenfell")

    private fun invoice(
        lines: List<InvoiceLineItem>,
        vat: String = "0",
    ) = InvoiceDocument(
        from = from,
        to = to,
        invoiceNumber = "INV-2026-0007",
        issueDate = "1 September 2026",
        dueDate = "30 September 2026",
        lineItems = lines,
        vatPercent = BigDecimal(vat),
    )

    private fun line(qty: String, price: String) =
        InvoiceLineItem("Delivery", BigDecimal(qty), BigDecimal(price))

    @Test
    fun roundingIsHalfAWAYfromZeroNotBankers() {
        // The default BigDecimal rounding is HALF_EVEN, which sends 2.125 down to
        // 2.12 and 2.135 down to 2.14 - agreeing with the C# on one and
        // disagreeing on the other. The C# passes AwayFromZero explicitly.
        assertEquals(BigDecimal("2.13"), InvoiceDocument.round2(BigDecimal("2.125")))
        assertEquals(BigDecimal("2.14"), InvoiceDocument.round2(BigDecimal("2.135")))
        assertEquals(BigDecimal("0.01"), InvoiceDocument.round2(BigDecimal("0.005")))
    }

    @Test
    fun awayFromZeroMeansAWAYnotUPWARDSOnACredit() {
        // HALF_UP is defined as away from zero, so a credit line rounds to the
        // larger magnitude too. CEILING would give -2.12 and quietly favour one
        // side of every credit note.
        assertEquals(BigDecimal("-2.13"), InvoiceDocument.round2(BigDecimal("-2.125")))
    }

    @Test
    fun everyLineIsRoundedBEFOREtheLinesAreSummed() {
        // Three lines at 0.125 each. Rounded per line: 0.13 x 3 = 0.39.
        // Summed first: 0.375, rounded once: 0.38. The invoice has to agree with
        // the numbers a person can add up down the right-hand column, so it is
        // 0.39 - one cent away from the mathematically tidier answer.
        val inv = invoice(listOf(line("1", "0.125"), line("1", "0.125"), line("1", "0.125")))
        assertEquals(BigDecimal("0.39"), inv.subtotal)
    }

    @Test
    fun aRealBasketAddsUpToTheCentWithVat() {
        val inv = invoice(listOf(line("12", "145.50"), line("3", "89.99")), vat = "15")
        assertEquals(BigDecimal("2015.97"), inv.subtotal)
        assertEquals(BigDecimal("302.40"), inv.vatAmount)
        assertEquals(BigDecimal("2318.37"), inv.total)
    }

    @Test
    fun totalsCarryTwOplacesEvenWhenTheyAreRound() {
        // Scale is not cosmetic here: an invoice prints what the BigDecimal
        // carries, and R1 800 has to read 1800.00 rather than 1800.
        val inv = invoice(listOf(line("2", "900")), vat = "0")
        assertEquals(2, inv.subtotal.scale())
        assertEquals(2, inv.vatAmount.scale())
        assertEquals(2, inv.total.scale())
        assertEquals("1800.00", inv.total.toPlainString())
    }

    @Test
    fun anEmptyInvoiceIsZeroNotAnError() {
        val inv = invoice(emptyList(), vat = "15")
        assertEquals(BigDecimal("0.00"), inv.subtotal)
        assertEquals(BigDecimal("0.00"), inv.vatAmount)
        assertEquals(BigDecimal("0.00"), inv.total)
    }

    @Test
    fun zeroVatLeavesTheSubtotalUntouched() {
        val inv = invoice(listOf(line("1", "199.99")))
        assertEquals(BigDecimal("199.99"), inv.subtotal)
        assertEquals(BigDecimal("0.00"), inv.vatAmount)
        assertEquals(BigDecimal("199.99"), inv.total)
    }

    @Test
    fun aFractionalVatRateStillDividesExactly() {
        // vat / 100 always terminates for a finite decimal, so the unrounded
        // divide can never throw here - pinned because it would throw for any
        // divisor that did not.
        val inv = invoice(listOf(line("1", "100.00")), vat = "12.5")
        assertEquals(BigDecimal("12.50"), inv.vatAmount)
        assertEquals(BigDecimal("112.50"), inv.total)
    }

    @Test
    fun aLineTotalIsQuantityTimesPriceBeforeAnyRounding() {
        assertEquals(BigDecimal("0.375"), line("3", "0.125").lineTotal)
    }

    @Test
    fun theMinimalInvoiceDefaultsToRandsAndNoVat() {
        val inv = InvoiceDocument.minimal(from, to, "INV-1", "1 Sep 2026", "30 Sep 2026")
        assertEquals("ZAR", inv.currencyCode)
        assertEquals(BigDecimal.ZERO, inv.vatPercent)
        assertTrue(inv.lineItems.isEmpty())
    }
}

class ReportModelTest {

    @Test
    fun aSectionCanBeProseBulletsATableOrAnyMixture() {
        val s = ReportSection(
            heading = "Deliveries",
            paragraphs = listOf("Volumes held through the strike."),
            table = ReportTable(
                columns = listOf("Route", "Drops"),
                rows = listOf(listOf("N1 North", "42"), listOf("N2 East", "17")),
                caption = "August 2026",
            ),
        )
        assertEquals(2, s.table!!.rows.size)
        assertEquals("August 2026", s.table!!.caption)

        // Null, not empty: a section with no bullets does not print an empty list.
        assertEquals(null, s.bullets)
    }

    @Test
    fun theMinimalReportIsATitle() {
        val r = ReportDocument.minimal("Fleet review")
        assertEquals("Fleet review", r.title)
        assertTrue(r.sections.isEmpty())
        assertEquals(null, r.author)
    }
}
