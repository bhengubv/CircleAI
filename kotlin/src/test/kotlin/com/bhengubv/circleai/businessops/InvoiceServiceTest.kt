package com.bhengubv.circleai.businessops

import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class InvoiceServiceTest {

    private val issue = LocalDate.of(2026, 7, 1)
    private val clock = FixedBusinessClock(Instant.ofEpochSecond(1_782_896_400L))

    private fun money(v: String) = Money.of(BigDecimal(v), "ZAR")!!

    private fun line(v: String, qty: String = "1", tax: String = "0") = BusinessInvoiceLine(
        description = "Work",
        quantity = BigDecimal(qty),
        unitPrice = Money.of(BigDecimal(v), "ZAR")!!,
        taxRate = BigDecimal(tax),
    )

    private class Counter(private var n: Int = 0) : IInvoiceNumberGenerator {
        override fun next(): String = "INV-TEST-" + (++n)
    }

    private fun svc(store: IBusinessStore = InMemoryBusinessStore()) =
        InvoiceService(store, Counter(), clock)

    // ---------------------------------------------------------- drafting

    @Test
    fun aDraftTakesTheDueDateFromTheClientPaymentTerms() = runTest {
        val store = InMemoryBusinessStore()
        store.clients.upsert(Client(clientId = "c1", name = "Thabo Trading", paymentTermsDays = 14))
        val inv = svc(store).createDraft("c1", "ZAR", listOf(line("1000")), issue)
        assertEquals(LocalDate.of(2026, 7, 15), inv.dueDate)
        assertEquals(InvoiceStatus.DRAFT, inv.status)
    }

    @Test
    fun explicitTermsBeatTheClientRecord() = runTest {
        val store = InMemoryBusinessStore()
        store.clients.upsert(Client(clientId = "c1", name = "Thabo Trading", paymentTermsDays = 14))
        val inv = svc(store).createDraft("c1", "ZAR", listOf(line("1000")), issue, paymentTermsDays = 7)
        assertEquals(LocalDate.of(2026, 7, 8), inv.dueDate)
    }

    @Test
    fun anUnknownClientFallsBackToThirtyDaysRatherThanFailing() = runTest {
        // Drafting for somebody not yet in the book has to work: the invoice is
        // often what makes you write the client record down.
        val inv = svc().createDraft("never-seen", "ZAR", listOf(line("1000")), issue)
        assertEquals(LocalDate.of(2026, 7, 31), inv.dueDate)
    }

    @Test
    fun theCurrencyIsNormalisedToUppercase() = runTest {
        val inv = svc().createDraft("c1", "  zar ", emptyList(), issue)
        assertEquals("ZAR", inv.currency)
    }

    @Test
    fun aMixedCurrencyInvoiceIsRefusedAtCreationNotLater() = runTest {
        // Caught later, this is a total that silently means nothing - on an
        // invoice that has already been sent.
        val ngn = BusinessInvoiceLine("Lagos work", BigDecimal.ONE, Money.of(1000L, "NGN")!!)
        val e = assertFailsWith<BusinessOpsError.LineCurrencyMismatch> {
            svc().createDraft("c1", "ZAR", listOf(line("1000"), ngn), issue)
        }
        assertEquals("NGN", e.lineCurrency)
        assertEquals("ZAR", e.invoiceCurrency)
    }

    @Test
    fun aBlankClientOrCurrencyIsRefused() = runTest {
        assertFailsWith<BusinessOpsError.MissingField> { svc().createDraft(" ", "ZAR", emptyList(), issue) }
        assertFailsWith<BusinessOpsError.MissingField> { svc().createDraft("c1", "  ", emptyList(), issue) }
    }

    @Test
    fun aDraftStartsWithNoNumberAndNothingPaid() = runTest {
        val inv = svc().createDraft("c1", "ZAR", listOf(line("1000")), issue)
        assertNull(inv.number)
        assertTrue(inv.paidToDate.isZero)
        assertEquals(money("1000.00"), inv.balanceDue)
    }

    // ------------------------------------------------------------ issue

    @Test
    fun issuingAssignsANumberAndMovesItToSent() = runTest {
        val s = svc()
        val draft = s.createDraft("c1", "ZAR", listOf(line("1000")), issue)
        val sent = s.issue(draft.invoiceId)
        assertEquals("INV-TEST-1", sent.number)
        assertEquals(InvoiceStatus.SENT, sent.status)
    }

    @Test
    fun reIssuingDoesNotRENUMBERit() = runTest {
        // The customer is already holding the old number. Renumbering on a
        // second issue means two documents claiming to be the same invoice.
        val s = svc()
        val draft = s.createDraft("c1", "ZAR", listOf(line("1000")), issue)
        val first = s.issue(draft.invoiceId)
        val again = s.issue(draft.invoiceId)
        assertEquals(first.number, again.number)
        assertEquals("INV-TEST-1", again.number)
    }

    @Test
    fun aCancelledInvoiceCannotBeIssued() = runTest {
        val s = svc()
        val draft = s.createDraft("c1", "ZAR", listOf(line("1000")), issue)
        s.cancel(draft.invoiceId)
        assertFailsWith<BusinessOpsError.CancelledCannotBeIssued> { s.issue(draft.invoiceId) }
    }

    @Test
    fun issuingAnUnknownInvoiceNamesTheIdItCouldNotFind() = runTest {
        val e = assertFailsWith<BusinessOpsError.InvoiceNotFound> { svc().issue("nope") }
        assertEquals("nope", e.invoiceId)
    }

    @Test
    fun aBlankInvoiceIdIsAMissingFieldNotANotFound() = runTest {
        assertFailsWith<BusinessOpsError.MissingField> { svc().issue("  ") }
    }

    // --------------------------------------------------------- payments

    @Test
    fun aPartialPaymentLeavesABalanceAndSaysSo() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        val after = s.recordPayment(inv.invoiceId, money("400"))
        assertEquals(InvoiceStatus.PARTIALLY_PAID, after.status)
        assertEquals(money("600.00"), after.balanceDue)
        assertFalse(after.isSettled)
    }

    @Test
    fun paymentsACCUMULATEratherThanReplace() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.recordPayment(inv.invoiceId, money("400"))
        val after = s.recordPayment(inv.invoiceId, money("600"))
        assertEquals(InvoiceStatus.PAID, after.status)
        assertTrue(after.isSettled)
    }

    @Test
    fun anOverpaymentStillSettlesRatherThanStickingAtPartiallyPaid() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        val after = s.recordPayment(inv.invoiceId, money("1500"))
        assertEquals(InvoiceStatus.PAID, after.status)
        assertEquals(money("-500.00"), after.balanceDue)
    }

    @Test
    fun aPaymentInTheWrongCurrencyIsRefused() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        val e = assertFailsWith<BusinessOpsError.PaymentCurrencyMismatch> {
            s.recordPayment(inv.invoiceId, Money.of(1000L, "NGN")!!)
        }
        assertEquals("NGN", e.payment)
        assertEquals("ZAR", e.invoice)
    }

    @Test
    fun aZeroOrNegativePaymentIsRefused() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        assertFailsWith<BusinessOpsError.PaymentMustBePositive> {
            s.recordPayment(inv.invoiceId, money("0"))
        }
        assertFailsWith<BusinessOpsError.PaymentMustBePositive> {
            s.recordPayment(inv.invoiceId, money("-100"))
        }
    }

    @Test
    fun aCancelledInvoiceCannotBePaid() = runTest {
        val s = svc()
        val inv = s.createDraft("c1", "ZAR", listOf(line("1000")), issue)
        s.cancel(inv.invoiceId)
        assertFailsWith<BusinessOpsError.CancelledCannotBePaid> {
            s.recordPayment(inv.invoiceId, money("100"))
        }
        assertFailsWith<BusinessOpsError.CancelledCannotBePaid> { s.markPaid(inv.invoiceId) }
    }

    @Test
    fun markPaidSettlesWhateverIsLeft() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.recordPayment(inv.invoiceId, money("400"))
        val after = s.markPaid(inv.invoiceId)
        assertEquals(InvoiceStatus.PAID, after.status)
        assertEquals(money("1000.00"), after.paidToDate)
    }

    @Test
    fun markPaidOnAnAlreadySettledInvoiceDoesNotTryToPayZero() = runTest {
        // recordPayment refuses a zero amount, so a naive markPaid would throw on
        // the second call. It must be idempotent instead.
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.markPaid(inv.invoiceId)
        val again = s.markPaid(inv.invoiceId)
        assertEquals(InvoiceStatus.PAID, again.status)
        assertEquals(money("1000.00"), again.paidToDate)
    }

    @Test
    fun markPaidOnAnEmptyInvoiceMarksItPaidWithoutAPayment() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", emptyList(), issue).invoiceId)
        val after = s.markPaid(inv.invoiceId)
        assertEquals(InvoiceStatus.PAID, after.status)
        assertTrue(after.paidToDate.isZero)
    }

    // ----------------------------------------------------------- cancel

    @Test
    fun aPaidInvoiceCannotBeCancelled() = runTest {
        // The money has moved. Cancelling would erase a record the bank still
        // has; the answer is a credit note, and the error says so.
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.markPaid(inv.invoiceId)
        assertFailsWith<BusinessOpsError.PaidCannotBeCancelled> { s.cancel(inv.invoiceId) }
    }

    @Test
    fun aDraftCanBeCancelled() = runTest {
        val s = svc()
        val inv = s.createDraft("c1", "ZAR", listOf(line("1000")), issue)
        assertEquals(InvoiceStatus.CANCELLED, s.cancel(inv.invoiceId).status)
    }

    // ------------------------------------------------------------ lists

    @Test
    fun listingIsNewestFirstByIssueDate() = runTest {
        val s = svc()
        s.createDraft("c1", "ZAR", emptyList(), LocalDate.of(2026, 1, 15))
        s.createDraft("c1", "ZAR", emptyList(), LocalDate.of(2026, 8, 1))
        s.createDraft("c1", "ZAR", emptyList(), LocalDate.of(2026, 4, 2))
        assertEquals(
            listOf(LocalDate.of(2026, 8, 1), LocalDate.of(2026, 4, 2), LocalDate.of(2026, 1, 15)),
            s.list().map { it.issueDate },
        )
    }

    @Test
    fun listingCanBeFilteredByStatus() = runTest {
        val s = svc()
        val a = s.createDraft("c1", "ZAR", emptyList(), issue)
        s.createDraft("c1", "ZAR", emptyList(), issue)
        s.issue(a.invoiceId)
        assertEquals(1, s.list(InvoiceStatus.SENT).size)
        assertEquals(1, s.list(InvoiceStatus.DRAFT).size)
        assertEquals(2, s.list().size)
    }

    @Test
    fun listByClientKeepsOneBusinessOutOfAnotherView() = runTest {
        val s = svc()
        s.createDraft("c1", "ZAR", emptyList(), issue)
        s.createDraft("c2", "ZAR", emptyList(), issue)
        assertEquals(1, s.listByClient("c1").size)
        assertTrue(s.listByClient("c3").isEmpty())
        assertFailsWith<BusinessOpsError.MissingField> { s.listByClient(" ") }
    }

    // ---------------------------------------------------------- overdue

    @Test
    fun aDraftIsNeverOverdueBecauseItWasNeverSENT() = runTest {
        val s = svc()
        s.createDraft("c1", "ZAR", listOf(line("1000")), issue)
        assertTrue(s.listOverdue(LocalDate.of(2027, 1, 1)).isEmpty())
    }

    @Test
    fun aSentInvoicePastItsDueDateIsOverdue() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        assertTrue(s.listOverdue(inv.dueDate!!).isEmpty(), "due today is not yet late")
        assertEquals(1, s.listOverdue(inv.dueDate!!.plusDays(1)).size)
    }

    @Test
    fun aSettledInvoiceIsNotOverdueHoweverLateItWasPaid() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.markPaid(inv.invoiceId)
        assertTrue(s.listOverdue(LocalDate.of(2027, 1, 1)).isEmpty())
    }

    @Test
    fun aCancelledInvoiceIsNotOverdue() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.cancel(inv.invoiceId)
        assertTrue(s.listOverdue(LocalDate.of(2027, 1, 1)).isEmpty())
    }

    @Test
    fun overdueListingIsOldestDueDateFirstBecauseThatIsWhoToChase() = runTest {
        val s = svc()
        val a = s.issue(s.createDraft("c1", "ZAR", listOf(line("1")), LocalDate.of(2026, 3, 1)).invoiceId)
        val b = s.issue(s.createDraft("c1", "ZAR", listOf(line("1")), LocalDate.of(2026, 1, 1)).invoiceId)
        val order = s.listOverdue(LocalDate.of(2026, 12, 1)).map { it.invoiceId }
        assertEquals(listOf(b.invoiceId, a.invoiceId), order)
    }

    @Test
    fun refreshOverdueStampsTheStatusAndCountsWhatChanged() = runTest {
        val s = svc()
        s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        assertEquals(1, s.refreshOverdue(LocalDate.of(2027, 1, 1)))
        assertEquals(InvoiceStatus.OVERDUE, s.list().first().status)

        // Idempotent: running it again changes nothing, so a caller can run it
        // on every app open without generating a second round of nagging.
        assertEquals(0, s.refreshOverdue(LocalDate.of(2027, 1, 1)))
    }

    @Test
    fun anOverdueInvoiceThatGetsPaidStopsBeingOverdue() = runTest {
        val s = svc()
        val inv = s.issue(s.createDraft("c1", "ZAR", listOf(line("1000")), issue).invoiceId)
        s.refreshOverdue(LocalDate.of(2027, 1, 1))
        val paid = s.markPaid(inv.invoiceId)
        assertEquals(InvoiceStatus.PAID, paid.status)
        assertTrue(s.listOverdue(LocalDate.of(2027, 1, 1)).isEmpty())
    }
}

class SequentialInvoiceNumberGeneratorTest {

    @Test
    fun numbersAreSequentialGaplessAndZeroPadded() {
        val g = SequentialInvoiceNumberGenerator(year = 2026)
        assertEquals("INV-2026-0001", g.next())
        assertEquals("INV-2026-0002", g.next())
        assertEquals("INV-2026-0003", g.next())
    }

    @Test
    fun aSeedResumesRatherThanRestarting() {
        // Reinstalling the app must not start invoice numbering again at 0001.
        val g = SequentialInvoiceNumberGenerator(year = 2026, seed = 41)
        assertEquals("INV-2026-0042", g.next())
    }

    @Test
    fun paddingGivesWayRatherThanTruncatingPastFourDigits() {
        val g = SequentialInvoiceNumberGenerator(year = 2026, seed = 9999)
        assertEquals("INV-2026-10000", g.next())
    }

    @Test
    fun thePrefixIsConfigurableForABusinessWithItsOwnScheme() {
        assertEquals("QUO-2026-0001", SequentialInvoiceNumberGenerator("QUO-", 2026).next())
    }
}

class NullInvoicePdfRendererTest {

    @Test
    fun itRefusesLoudlyRatherThanReturningABlankPage() = runTest {
        // A blank PDF would be worse than an error, because somebody would send it.
        assertFailsWith<BusinessOpsError.NoPdfRenderer> {
            NullInvoicePdfRenderer.instance.render(BusinessOpsSampleData.sampleInvoice(), null)
        }
        assertEquals("null", NullInvoicePdfRenderer.instance.backendId)
    }
}
