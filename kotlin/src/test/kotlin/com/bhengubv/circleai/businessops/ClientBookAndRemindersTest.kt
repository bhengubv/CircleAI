package com.bhengubv.circleai.businessops

import com.bhengubv.circleai.crm.Contact
import com.bhengubv.circleai.crm.IContactStore
import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.test.runTest

class ClientBookTest {

    private val now = Instant.ofEpochSecond(1_782_896_400L)
    private fun book(store: IBusinessStore = InMemoryBusinessStore()) =
        ClientBook(store, FixedBusinessClock(now))

    @Test
    fun upsertStampsACreationTimeOnce() = runTest {
        val b = book()
        val saved = b.upsert(Client(clientId = "c1", name = "Nandi Dlamini Design"))
        assertEquals(now, saved.createdAtUtc)

        // Editing later must not rewrite when the relationship started.
        val later = ClientBook(
            InMemoryBusinessStore(),
            FixedBusinessClock(now.plus(400, ChronoUnit.DAYS)),
        ).upsert(saved.copy(name = "Nandi Dlamini Studio"))
        assertEquals(now, later.createdAtUtc)
    }

    @Test
    fun aBlankClientIdIsRefused() = runTest {
        assertFailsWith<BusinessOpsError.MissingField> { book().upsert(Client(" ", "Nobody")) }
    }

    @Test
    fun searchFindsThemByNameEmailOrPhone() = runTest {
        // The three things somebody actually remembers about a customer.
        val b = book()
        b.upsert(
            Client(
                clientId = "c1",
                name = "Nandi Dlamini Design",
                email = "nandi@example.co.za",
                phone = "+27 82 555 0142",
            ),
        )
        assertEquals(1, b.search("dlamini").size)
        assertEquals(1, b.search("example.co.za").size)
        assertEquals(1, b.search("555 0142").size)
        assertTrue(b.search("mokoena").isEmpty())
    }

    @Test
    fun searchIsCaseInsensitive() = runTest {
        val b = book()
        b.upsert(Client(clientId = "c1", name = "Nandi Dlamini Design"))
        assertEquals(1, b.search("NANDI").size)
    }

    @Test
    fun aClientWithNoEmailOrPhoneDoesNotBreakSearch() = runTest {
        // Both fields are nullable and most walk-in customers have neither.
        val b = book()
        b.upsert(Client(clientId = "c1", name = "Cash customer"))
        assertEquals(1, b.search("cash").size)
        assertTrue(b.search("@").isEmpty())
    }

    @Test
    fun topKCapsTheResultsAndZeroReturnsNothing() = runTest {
        val b = book()
        repeat(5) { b.upsert(Client(clientId = "c" + it, name = "Trader " + it)) }
        assertEquals(2, b.search("trader", topK = 2).size)
        assertTrue(b.search("trader", topK = 0).isEmpty())
        assertTrue(b.search("trader", topK = -1).isEmpty())
    }

    @Test
    fun listingIsAlphabeticalByNameIgnoringCase() = runTest {
        val b = book()
        b.upsert(Client(clientId = "c1", name = "thabo Trading"))
        b.upsert(Client(clientId = "c2", name = "Amara Studios"))
        b.upsert(Client(clientId = "c3", name = "Nandi Design"))
        assertEquals(
            listOf("Amara Studios", "Nandi Design", "thabo Trading"),
            b.list().map { it.name },
        )
    }

    @Test
    fun removeReportsWhetherThereWasAnythingToRemove() = runTest {
        val b = book()
        b.upsert(Client(clientId = "c1", name = "Nandi"))
        assertTrue(b.remove("c1"))
        assertFalse(b.remove("c1"))
        assertNull(b.get("c1"))
    }
}

class ReminderSchedulerTest {

    private val now = Instant.ofEpochSecond(1_782_896_400L) // 2026-07-01T09:00:00Z
    private val due = Instant.ofEpochSecond(1_784_534_400L) // 2026-07-20T08:00:00Z

    private fun sched(store: IBusinessStore = InMemoryBusinessStore()) =
        ReminderScheduler(store, FixedBusinessClock(now))

    private fun reminder(
        id: String = "r1",
        title: String = "Chase the invoice",
        at: Instant = due,
        rule: RecurrenceRule = RecurrenceRule.ONCE,
    ) = Reminder(reminderId = id, title = title, dueAtUtc = at, repeatRule = rule)

    @Test
    fun schedulingStampsACreationTime() = runTest {
        assertEquals(now, sched().schedule(reminder()).createdAtUtc)
    }

    @Test
    fun anExistingCreationTimeIsLeftAlone() = runTest {
        val old = now.minus(30, ChronoUnit.DAYS)
        val r = sched().schedule(reminder().copy(createdAtUtc = old))
        assertEquals(old, r.createdAtUtc)
    }

    @Test
    fun aBlankIdOrTitleIsRefused() = runTest {
        assertFailsWith<BusinessOpsError.MissingField> { sched().schedule(reminder(id = " ")) }
        assertFailsWith<BusinessOpsError.MissingField> { sched().schedule(reminder(title = "  ")) }
    }

    @Test
    fun aFollowUpIsBornWithAnIdAndPointsAtWhatItIsAbout() = runTest {
        val r = sched().scheduleFollowUp("inv-1", "Chase INV-2026-0001", due)
        assertTrue(r.reminderId.isNotBlank())
        assertEquals("inv-1", r.relatedEntityId)
        assertEquals(ReminderKind.FOLLOW_UP, r.kind)
        assertFalse(r.repeatRule.isRecurring)
    }

    @Test
    fun aFollowUpWithNoEntityOrTitleIsRefused() = runTest {
        assertFailsWith<BusinessOpsError.MissingField> { sched().scheduleFollowUp(" ", "T", due) }
        assertFailsWith<BusinessOpsError.MissingField> { sched().scheduleFollowUp("e", " ", due) }
    }

    @Test
    fun completingAOneOffReturnsNothingFurther() = runTest {
        val s = sched()
        s.schedule(reminder())
        assertNull(s.complete("r1"))
        assertTrue(s.get("r1")!!.completed)
    }

    @Test
    fun completingARecurringOneSchedulesTheNEXToccurrence() = runTest {
        // A repeating reminder that stops after the first tick is just a reminder.
        val s = sched()
        s.schedule(reminder(rule = RecurrenceRule(Recurrence.MONTHLY)))
        val next = s.complete("r1")
        assertNotNull(next)
        assertFalse(next.completed)
        assertNotEquals("r1", next.reminderId)
        assertEquals(due.plus(31, ChronoUnit.DAYS), next.dueAtUtc)
        assertTrue(s.get("r1")!!.completed)
    }

    @Test
    fun theNextOccurrenceIsMeasuredFromTheDUEdateNotFromNow() = runTest {
        // Completed four days late, a monthly reminder must still land on the
        // same day of the following month rather than walking forward every time.
        val lateClock = FixedBusinessClock(due.plus(4, ChronoUnit.DAYS))
        val store = InMemoryBusinessStore()
        val s = ReminderScheduler(store, lateClock)
        s.schedule(reminder(rule = RecurrenceRule(Recurrence.MONTHLY)))
        val next = s.complete("r1")!!
        assertEquals(due.plus(31, ChronoUnit.DAYS), next.dueAtUtc)
        // The follow-on carries the completion time as its creation stamp.
        assertEquals(due.plus(4, ChronoUnit.DAYS), next.createdAtUtc)
    }

    @Test
    fun aWeeklyRuleWithAnIntervalStepsByThatManyWeeks() = runTest {
        val s = sched()
        s.schedule(reminder(rule = RecurrenceRule(Recurrence.WEEKLY, 2)))
        assertEquals(due.plus(14, ChronoUnit.DAYS), s.complete("r1")!!.dueAtUtc)
    }

    @Test
    fun completingSomethingThatIsNotThereNamesIt() = runTest {
        val e = assertFailsWith<BusinessOpsError.ReminderNotFound> { sched().complete("ghost") }
        assertEquals("ghost", e.reminderId)
        assertFailsWith<BusinessOpsError.MissingField> { sched().complete(" ") }
    }

    @Test
    fun cancelReportsWhetherThereWasAnythingToCancel() = runTest {
        val s = sched()
        s.schedule(reminder())
        assertTrue(s.cancel("r1"))
        assertFalse(s.cancel("r1"))
    }

    @Test
    fun listDueIsSoonestFirstAndExcludesTheFutureAndTheDone() = runTest {
        val s = sched()
        s.schedule(reminder(id = "late", at = due.minus(2, ChronoUnit.DAYS)))
        s.schedule(reminder(id = "now", at = due))
        s.schedule(reminder(id = "later", at = due.plus(5, ChronoUnit.DAYS)))
        s.schedule(reminder(id = "done", at = due.minus(9, ChronoUnit.DAYS)))
        s.complete("done")

        assertEquals(listOf("late", "now"), s.listDue(due).map { it.reminderId })
    }

    @Test
    fun listPendingIncludesTheFutureButNotTheCompleted() = runTest {
        val s = sched()
        s.schedule(reminder(id = "a", at = due.plus(9, ChronoUnit.DAYS)))
        s.schedule(reminder(id = "b", at = due))
        s.schedule(reminder(id = "c"))
        s.complete("c")
        assertEquals(listOf("b", "a"), s.listPending().map { it.reminderId })
    }

    @Test
    fun listForEntityKeepsOneInvoiceRemindersOffAnother() = runTest {
        val s = sched()
        s.scheduleFollowUp("inv-1", "Chase 1", due)
        s.scheduleFollowUp("inv-1", "Chase 1 again", due.plus(7, ChronoUnit.DAYS))
        s.scheduleFollowUp("inv-2", "Chase 2", due)
        assertEquals(2, s.listForEntity("inv-1").size)
        assertEquals(1, s.listForEntity("inv-2").size)
        assertTrue(s.listForEntity("inv-3").isEmpty())
        assertFailsWith<BusinessOpsError.MissingField> { s.listForEntity(" ") }
    }
}

class CrmBridgeTest {

    private class FakeContacts : IContactStore {
        val saved = LinkedHashMap<String, Contact>()
        override val backendId: String get() = "fake"
        override suspend fun upsertAsync(c: Contact) { saved[c.contactId] = c }
        override suspend fun getAsync(id: String): Contact? = saved[id]
        override suspend fun searchAsync(query: String, topK: Int): List<Contact> =
            saved.values.filter { it.fullName.contains(query, ignoreCase = true) }.take(topK)
    }

    @Test
    fun aClientConvertsToAContactKeepingTheSameId() {
        // The SAME id on both sides is what makes this a bridge rather than a
        // copy: an email corrected in one place is not stale in the other.
        val c = Client(
            clientId = "cl-nandi",
            name = "Nandi Dlamini Design",
            email = "nandi@example.co.za",
            phone = "+27 82 555 0142",
        )
        val contact = c.toContact("co-1")
        assertEquals("cl-nandi", contact.contactId)
        assertEquals("Nandi Dlamini Design", contact.fullName)
        assertEquals("nandi@example.co.za", contact.email)
        assertEquals("co-1", contact.companyId)
    }

    @Test
    fun aContactConvertsBackWithSensibleBillingDefaults() {
        val contact = Contact("cl-thabo", "Thabo Trading CC", "a@b.example", "071", null)
        val client = contact.toClient()
        assertEquals("cl-thabo", client.clientId)
        assertEquals(Currencies.DEFAULT_CURRENCY, client.defaultCurrency)
        assertEquals(30, client.paymentTermsDays)
    }

    @Test
    fun aRoundTripThroughTheCrmKeepsTheIdentifyingFields() {
        val original = Client(
            clientId = "cl-amara",
            name = "Amara Studios (Lagos)",
            email = "hello@amara.example",
            phone = "+234 802 555 0101",
        )
        val back = original.toContact().toClient()
        assertEquals(original.clientId, back.clientId)
        assertEquals(original.name, back.name)
        assertEquals(original.email, back.email)
        assertEquals(original.phone, back.phone)
    }

    @Test
    fun aReminderBecomesATimelineActivityOnTheContact() {
        val r = Reminder(
            reminderId = "rem-1",
            title = "Monthly check-in call",
            dueAtUtc = Instant.ofEpochSecond(1_785_571_200L),
            kind = ReminderKind.FOLLOW_UP,
        )
        val a = r.toActivity("cl-thabo")
        assertEquals("rem-1", a.activityId)
        assertEquals("cl-thabo", a.contactId)
        assertEquals("FollowUp", a.kind)
        assertEquals("Monthly check-in call", a.body)
        assertEquals(r.dueAtUtc, a.atUtc)
    }

    @Test
    fun mirroringCopiesEveryClientAndCountsThem() = runTest {
        val store = InMemoryBusinessStore()
        val book = ClientBook(store, FixedBusinessClock(Instant.EPOCH))
        for (c in BusinessOpsSampleData.clients()) book.upsert(c)

        val contacts = FakeContacts()
        assertEquals(3, CrmBridge.mirrorToCrm(book, contacts))
        assertEquals(3, contacts.saved.size)
        assertEquals("Amara Studios (Lagos)", contacts.saved["cl-amara"]!!.fullName)
    }

    @Test
    fun mirroringAnEmptyBookIsZeroNotAnError() = runTest {
        val contacts = FakeContacts()
        assertEquals(0, CrmBridge.mirrorToCrm(NullClientBook.instance, contacts))
        assertTrue(contacts.saved.isEmpty())
    }
}

class BusinessOpsSampleDataTest {

    @Test
    fun theSampleClientsSpanTwoCurrenciesAndTwoPaymentTerms() {
        // The point of the sample: a demo screen should show what the module
        // has to handle, not three copies of the easy case.
        val cs = BusinessOpsSampleData.clients()
        assertEquals(3, cs.size)
        assertEquals(setOf("ZAR", "NGN"), cs.map { it.defaultCurrency }.toSet())
        assertEquals(setOf(30, 14), cs.map { it.paymentTermsDays }.toSet())
    }

    @Test
    fun theSampleInvoiceAddsUpWithFifteenPercentVat() {
        // 8500 + 2 x 750 = 10000 net, VAT 1500, total 11500.
        val inv = BusinessOpsSampleData.sampleInvoice()
        assertEquals(Money.of(BigDecimal("10000.00"), "ZAR"), inv.subtotal)
        assertEquals(Money.of(BigDecimal("1500.00"), "ZAR"), inv.taxTotal)
        assertEquals(Money.of(BigDecimal("11500.00"), "ZAR"), inv.total)
        assertEquals(Money.of(BigDecimal("11500.00"), "ZAR"), inv.balanceDue)
        assertFalse(inv.isSettled)
    }

    @Test
    fun theSampleInvoiceIsDueThirtyDaysAfterIssue() {
        val inv = BusinessOpsSampleData.sampleInvoice()
        assertEquals(LocalDate.of(2026, 7, 1), inv.issueDate)
        assertEquals(LocalDate.of(2026, 7, 31), inv.dueDate)
        assertEquals(InvoiceStatus.SENT, inv.status)
    }

    @Test
    fun theSampleRemindersCoverBothAOneOffAndARepeat() {
        val rs = BusinessOpsSampleData.reminders()
        assertEquals(2, rs.size)
        assertFalse(rs[0].repeatRule.isRecurring)
        assertTrue(rs[1].repeatRule.isRecurring)
        assertEquals(ReminderKind.INVOICE_DUE, rs[0].kind)
        assertEquals("inv-sample-1", rs[0].relatedEntityId)
    }

    @Test
    fun theSampleStampsAreTheDatesTheCommentsClaim() {
        // Hand-computed epoch seconds go wrong silently. This is the check.
        val inv = BusinessOpsSampleData.sampleInvoice()
        assertEquals("2026-07-01T09:00:00Z", inv.createdAtUtc.toString())
        assertEquals("2026-07-20T08:00:00Z", BusinessOpsSampleData.reminders()[0].dueAtUtc.toString())
        assertEquals("2026-08-01T08:00:00Z", BusinessOpsSampleData.reminders()[1].dueAtUtc.toString())
    }
}

class BusinessOpsNullsTest {

    @Test
    fun theNullStoreHoldsNothingOnEveryRepository() = runTest {
        val s = NullBusinessStore.instance
        assertEquals("null", s.backendId)
        s.clients.upsert(Client("c1", "Nobody"))
        assertNull(s.clients.get("c1"))
        assertTrue(s.invoices.list().isEmpty())
        assertFalse(s.reminders.remove("r1"))
    }

    @Test
    fun theNullClientBookIsAPassThroughThatRemembersNothing() = runTest {
        val b = NullClientBook.instance
        val c = Client("c1", "Nandi")
        assertEquals(c, b.upsert(c))
        assertNull(b.get("c1"))
        assertTrue(b.list().isEmpty())
    }

    @Test
    fun theNullInvoiceServiceReadsEmptyButREFUSEStoWrite() = runTest {
        // The asymmetry is the point. A read that returns nothing looks like a
        // business with no invoices, which is survivable. A write that quietly
        // succeeds looks like an invoice that was raised, and it was not.
        val s = NullInvoiceService.instance
        assertTrue(s.list().isEmpty())
        assertNull(s.get("x"))
        assertEquals(0, s.refreshOverdue(LocalDate.of(2026, 7, 1)))

        assertFailsWith<BusinessOpsError> {
            s.createDraft("c1", "ZAR", emptyList(), LocalDate.of(2026, 7, 1))
        }
        assertFailsWith<BusinessOpsError.InvoiceNotFound> { s.issue("x") }
        assertFailsWith<BusinessOpsError.InvoiceNotFound> { s.markPaid("x") }
        assertFailsWith<BusinessOpsError.InvoiceNotFound> { s.cancel("x") }
    }

    @Test
    fun theNullSchedulerNeverRemembersAndNeverFires() = runTest {
        val s = NullReminderScheduler.instance
        val r = Reminder("r1", "Chase", Instant.EPOCH)
        assertEquals(r, s.schedule(r))
        assertNull(s.get("r1"))
        assertTrue(s.listPending().isEmpty())
        assertNull(s.complete("r1"))
        assertFalse(s.cancel("r1"))
    }
}
