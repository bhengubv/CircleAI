// BusinessOpsServicesB.kt
//
// The three services, the CRM bridge, the fail-closed nulls and the sample
// data. The contracts and storage they sit on are in BusinessOpsServices.kt.

package com.bhengubv.circleai.businessops

import com.bhengubv.circleai.crm.Activity
import com.bhengubv.circleai.crm.Contact
import com.bhengubv.circleai.crm.IContactStore
import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate

/** Stamps a creation time on a client that has never been saved before. */
private fun Client.stamped(now: Instant): Client =
    if (createdAtUtc == null) copy(createdAtUtc = now) else this

class ClientBook(
    store: IBusinessStore,
    private val clock: IBusinessClock = SystemBusinessClock(),
) : IClientBook {

    private val repo: IClientRepository = store.clients

    override val backendId: String get() = "default"

    override suspend fun upsert(client: Client): Client {
        requireField(client.clientId, "clientId")
        val stamped = client.stamped(clock.now())
        repo.upsert(stamped)
        return stamped
    }

    override suspend fun get(clientId: String): Client? = repo.get(clientId)

    /** Name, email or phone - the three things somebody actually remembers. */
    override suspend fun search(query: String, topK: Int): List<Client> {
        if (topK <= 0) return emptyList()
        val q = query.lowercase()
        return repo.list().filter {
            it.name.lowercase().contains(q) ||
                (it.email?.lowercase()?.contains(q) ?: false) ||
                (it.phone?.lowercase()?.contains(q) ?: false)
        }.take(topK)
    }

    override suspend fun list(): List<Client> = repo.list()

    override suspend fun remove(clientId: String): Boolean = repo.remove(clientId)
}

class InvoiceService(
    store: IBusinessStore,
    private val numbers: IInvoiceNumberGenerator = SequentialInvoiceNumberGenerator(),
    private val clock: IBusinessClock = SystemBusinessClock(),
) : IInvoiceService {

    private val invoices: IInvoiceRepository = store.invoices
    private val clients: IClientRepository = store.clients

    override val backendId: String get() = "default"

    override suspend fun createDraft(
        clientId: String,
        currency: String,
        lines: List<BusinessInvoiceLine>,
        issueDate: LocalDate,
        paymentTermsDays: Int?,
        notes: String?,
    ): BusinessInvoice {
        requireField(clientId, "clientId")
        val cur = requireField(currency.trim().uppercase(), "currency")

        // Refused AT CREATION. A mixed-currency invoice caught later is a total
        // that silently means nothing, and it will already have been sent.
        for (l in lines) {
            if (l.unitPrice.currency != cur) {
                throw BusinessOpsError.LineCurrencyMismatch(l.description, l.unitPrice.currency, cur)
            }
        }

        // Explicit terms win; otherwise the terms on the client record;
        // otherwise thirty days.
        val terms = paymentTermsDays ?: (clients.get(clientId)?.paymentTermsDays ?: 30)
        val now = clock.now()
        val invoice = BusinessInvoice(
            invoiceId = BusinessOpsIds.new(),
            clientId = clientId,
            currency = cur,
            lines = lines,
            status = InvoiceStatus.DRAFT,
            issueDate = issueDate,
            dueDate = issueDate.plusDays(terms.toLong()),
            amountPaid = Money.zero(cur),
            notes = notes,
            createdAtUtc = now,
            updatedAtUtc = now,
        )
        invoices.upsert(invoice)
        return invoice
    }

    override suspend fun get(invoiceId: String): BusinessInvoice? = invoices.get(invoiceId)

    override suspend fun issue(
        invoiceId: String,
        issueDate: LocalDate?,
        paymentTermsDays: Int,
    ): BusinessInvoice {
        val inv = require(invoiceId)
        if (inv.status == InvoiceStatus.CANCELLED) throw BusinessOpsError.CancelledCannotBeIssued()

        val issue = issueDate ?: inv.issueDate ?: LocalDate.ofInstant(clock.now(), java.time.ZoneOffset.UTC)
        val due = inv.dueDate ?: issue.plusDays(paymentTermsDays.toLong())

        // The number is assigned ONCE, on first issue. Re-issuing must not
        // renumber it - the customer is already holding the old number.
        val updated = inv.copy(
            number = inv.number ?: numbers.next(),
            status = if (inv.status == InvoiceStatus.DRAFT) InvoiceStatus.SENT else inv.status,
            issueDate = issue,
            dueDate = due,
            updatedAtUtc = clock.now(),
        )
        invoices.upsert(updated)
        return updated
    }

    override suspend fun recordPayment(invoiceId: String, amount: Money): BusinessInvoice {
        val inv = require(invoiceId)
        if (inv.status == InvoiceStatus.CANCELLED) throw BusinessOpsError.CancelledCannotBePaid()
        if (amount.currency != inv.currency) {
            throw BusinessOpsError.PaymentCurrencyMismatch(amount.currency, inv.currency)
        }
        if (amount.amount.signum() <= 0) throw BusinessOpsError.PaymentMustBePositive()

        val paid = (inv.paidToDate + amount).rounded()
        val status =
            if (paid.amount >= inv.total.amount) InvoiceStatus.PAID else InvoiceStatus.PARTIALLY_PAID
        val updated = inv.copy(status = status, amountPaid = paid, updatedAtUtc = clock.now())
        invoices.upsert(updated)
        return updated
    }

    /**
     * Settles whatever is left. An already-settled invoice is simply marked
     * paid rather than handed a zero payment, which recordPayment would refuse.
     */
    override suspend fun markPaid(invoiceId: String): BusinessInvoice {
        val inv = require(invoiceId)
        if (inv.status == InvoiceStatus.CANCELLED) throw BusinessOpsError.CancelledCannotBePaid()

        val balance = inv.balanceDue
        if (balance.amount.signum() <= 0) {
            if (inv.status == InvoiceStatus.PAID) return inv
            val already = inv.copy(status = InvoiceStatus.PAID, updatedAtUtc = clock.now())
            invoices.upsert(already)
            return already
        }
        return recordPayment(invoiceId, balance)
    }

    override suspend fun cancel(invoiceId: String): BusinessInvoice {
        val inv = require(invoiceId)
        if (inv.status == InvoiceStatus.PAID) throw BusinessOpsError.PaidCannotBeCancelled()
        val updated = inv.copy(status = InvoiceStatus.CANCELLED, updatedAtUtc = clock.now())
        invoices.upsert(updated)
        return updated
    }

    override suspend fun list(status: InvoiceStatus?): List<BusinessInvoice> {
        val all = invoices.list()
        val filtered = if (status == null) all else all.filter { it.status == status }
        return filtered.sortedByDescending { it.issueDate ?: LocalDate.MIN }
    }

    override suspend fun listByClient(clientId: String): List<BusinessInvoice> {
        requireField(clientId, "clientId")
        return invoices.list()
            .filter { it.clientId == clientId }
            .sortedByDescending { it.issueDate ?: LocalDate.MIN }
    }

    override suspend fun listOverdue(asOf: LocalDate): List<BusinessInvoice> =
        invoices.list()
            .filter { it.isOverdue(asOf) }
            .sortedBy { it.dueDate ?: LocalDate.MAX }

    /**
     * Stamps OVERDUE onto anything past its due date and returns how many
     * changed, so a caller can decide whether there is anything worth saying.
     */
    override suspend fun refreshOverdue(asOf: LocalDate): Int {
        var changed = 0
        for (inv in invoices.list()) {
            if (inv.isOverdue(asOf) && inv.status != InvoiceStatus.OVERDUE) {
                invoices.upsert(inv.copy(status = InvoiceStatus.OVERDUE, updatedAtUtc = clock.now()))
                changed++
            }
        }
        return changed
    }

    private suspend fun require(invoiceId: String): BusinessInvoice {
        requireField(invoiceId, "invoiceId")
        return invoices.get(invoiceId) ?: throw BusinessOpsError.InvoiceNotFound(invoiceId)
    }
}

class ReminderScheduler(
    store: IBusinessStore,
    private val clock: IBusinessClock = SystemBusinessClock(),
) : IReminderScheduler {

    private val repo: IReminderRepository = store.reminders

    override val backendId: String get() = "default"

    override suspend fun schedule(reminder: Reminder): Reminder {
        requireField(reminder.reminderId, "reminderId")
        requireField(reminder.title, "title")
        val stamped =
            if (reminder.createdAtUtc == null) reminder.copy(createdAtUtc = clock.now())
            else reminder
        repo.upsert(stamped)
        return stamped
    }

    override suspend fun scheduleFollowUp(
        relatedEntityId: String,
        title: String,
        dueAtUtc: Instant,
        repeatRule: RecurrenceRule?,
    ): Reminder {
        requireField(relatedEntityId, "relatedEntityId")
        requireField(title, "title")
        return schedule(
            Reminder(
                reminderId = BusinessOpsIds.new(),
                title = title,
                dueAtUtc = dueAtUtc,
                repeatRule = repeatRule ?: RecurrenceRule.ONCE,
                kind = ReminderKind.FOLLOW_UP,
                relatedEntityId = relatedEntityId,
                createdAtUtc = clock.now(),
            ),
        )
    }

    override suspend fun get(reminderId: String): Reminder? = repo.get(reminderId)

    /**
     * Completing a recurring reminder schedules the NEXT one and returns it. A
     * repeating reminder that stops after the first tick is just a reminder.
     *
     * The next occurrence is measured from the DUE date, not from now, so a
     * monthly check-in completed four days late still falls on the same day of
     * the following month instead of walking forward by four days a month.
     */
    override suspend fun complete(reminderId: String): Reminder? {
        requireField(reminderId, "reminderId")
        val existing = repo.get(reminderId) ?: throw BusinessOpsError.ReminderNotFound(reminderId)
        repo.upsert(existing.copy(completed = true))

        if (!existing.repeatRule.isRecurring) return null
        val next = existing.repeatRule.next(existing.dueAtUtc) ?: return null

        val followOn = existing.copy(
            reminderId = BusinessOpsIds.new(),
            dueAtUtc = next,
            completed = false,
            createdAtUtc = clock.now(),
        )
        repo.upsert(followOn)
        return followOn
    }

    override suspend fun cancel(reminderId: String): Boolean = repo.remove(reminderId)

    override suspend fun listDue(asOf: Instant): List<Reminder> =
        repo.list().filter { it.isDue(asOf) }.sortedBy { it.dueAtUtc }

    override suspend fun listPending(): List<Reminder> =
        repo.list().filter { !it.completed }.sortedBy { it.dueAtUtc }

    override suspend fun listForEntity(relatedEntityId: String): List<Reminder> {
        requireField(relatedEntityId, "relatedEntityId")
        return repo.list()
            .filter { it.relatedEntityId == relatedEntityId }
            .sortedBy { it.dueAtUtc }
    }
}

// ------------------------------------------------------- Fail-closed

class NullBusinessStore : IBusinessStore {
    override val backendId: String get() = "null"
    override val clients: IClientRepository = Clients()
    override val invoices: IInvoiceRepository = Invoices()
    override val reminders: IReminderRepository = Reminders()

    private class Clients : IClientRepository {
        override suspend fun upsert(client: Client) {}
        override suspend fun get(clientId: String): Client? = null
        override suspend fun list(): List<Client> = emptyList()
        override suspend fun remove(clientId: String): Boolean = false
    }

    private class Invoices : IInvoiceRepository {
        override suspend fun upsert(invoice: BusinessInvoice) {}
        override suspend fun get(invoiceId: String): BusinessInvoice? = null
        override suspend fun list(): List<BusinessInvoice> = emptyList()
        override suspend fun remove(invoiceId: String): Boolean = false
    }

    private class Reminders : IReminderRepository {
        override suspend fun upsert(reminder: Reminder) {}
        override suspend fun get(reminderId: String): Reminder? = null
        override suspend fun list(): List<Reminder> = emptyList()
        override suspend fun remove(reminderId: String): Boolean = false
    }

    companion object { val instance = NullBusinessStore() }
}

class NullClientBook : IClientBook {
    override val backendId: String get() = "null"
    override suspend fun upsert(client: Client): Client = client
    override suspend fun get(clientId: String): Client? = null
    override suspend fun search(query: String, topK: Int): List<Client> = emptyList()
    override suspend fun list(): List<Client> = emptyList()
    override suspend fun remove(clientId: String): Boolean = false

    companion object { val instance = NullClientBook() }
}

/**
 * Every read is empty and every write REFUSES.
 *
 * The asymmetry is deliberate. A read that quietly returns nothing looks like a
 * business with no invoices, which is survivable. A write that quietly succeeds
 * looks like an invoice that was raised, and it was not.
 */
class NullInvoiceService : IInvoiceService {
    override val backendId: String get() = "null"

    override suspend fun createDraft(
        clientId: String,
        currency: String,
        lines: List<BusinessInvoiceLine>,
        issueDate: LocalDate,
        paymentTermsDays: Int?,
        notes: String?,
    ): BusinessInvoice = throw BusinessOpsError.MissingField("invoice store")

    override suspend fun get(invoiceId: String): BusinessInvoice? = null

    override suspend fun issue(
        invoiceId: String,
        issueDate: LocalDate?,
        paymentTermsDays: Int,
    ): BusinessInvoice = throw BusinessOpsError.InvoiceNotFound(invoiceId)

    override suspend fun recordPayment(invoiceId: String, amount: Money): BusinessInvoice =
        throw BusinessOpsError.InvoiceNotFound(invoiceId)

    override suspend fun markPaid(invoiceId: String): BusinessInvoice =
        throw BusinessOpsError.InvoiceNotFound(invoiceId)

    override suspend fun cancel(invoiceId: String): BusinessInvoice =
        throw BusinessOpsError.InvoiceNotFound(invoiceId)

    override suspend fun list(status: InvoiceStatus?): List<BusinessInvoice> = emptyList()
    override suspend fun listByClient(clientId: String): List<BusinessInvoice> = emptyList()
    override suspend fun listOverdue(asOf: LocalDate): List<BusinessInvoice> = emptyList()
    override suspend fun refreshOverdue(asOf: LocalDate): Int = 0

    companion object { val instance = NullInvoiceService() }
}

class NullReminderScheduler : IReminderScheduler {
    override val backendId: String get() = "null"
    override suspend fun schedule(reminder: Reminder): Reminder = reminder

    override suspend fun scheduleFollowUp(
        relatedEntityId: String,
        title: String,
        dueAtUtc: Instant,
        repeatRule: RecurrenceRule?,
    ): Reminder = Reminder(
        reminderId = "",
        title = title,
        dueAtUtc = dueAtUtc,
        repeatRule = repeatRule ?: RecurrenceRule.ONCE,
        kind = ReminderKind.FOLLOW_UP,
        relatedEntityId = relatedEntityId,
    )

    override suspend fun get(reminderId: String): Reminder? = null
    override suspend fun complete(reminderId: String): Reminder? = null
    override suspend fun cancel(reminderId: String): Boolean = false
    override suspend fun listDue(asOf: Instant): List<Reminder> = emptyList()
    override suspend fun listPending(): List<Reminder> = emptyList()
    override suspend fun listForEntity(relatedEntityId: String): List<Reminder> = emptyList()

    companion object { val instance = NullReminderScheduler() }
}

// --------------------------------------------------------- CRM bridge
//
// The same person is a Client here and a Contact in the CRM. These convert
// between the two rather than duplicating the record, so an email corrected in
// one place is not stale in the other.

fun Client.toContact(companyId: String? = null): Contact =
    Contact(contactId = clientId, fullName = name, email = email, phone = phone, companyId = companyId)

fun Contact.toClient(
    defaultCurrency: String = Currencies.DEFAULT_CURRENCY,
    paymentTermsDays: Int = 30,
): Client = Client(
    clientId = contactId,
    name = fullName,
    email = email,
    phone = phone,
    defaultCurrency = defaultCurrency,
    paymentTermsDays = paymentTermsDays,
)

fun Reminder.toActivity(contactId: String): Activity = Activity(
    activityId = reminderId,
    contactId = contactId,
    kind = kind.displayName,
    body = title,
    atUtc = dueAtUtc,
)

object CrmBridge {
    /** Copies every client into the CRM as a contact. Returns how many. */
    suspend fun mirrorToCrm(clients: IClientBook, contacts: IContactStore): Int {
        var n = 0
        for (client in clients.list()) {
            contacts.upsertAsync(client.toContact())
            n++
        }
        return n
    }
}

// -------------------------------------------------------- Sample data
//
// Three real-shaped clients across two currencies and two payment terms, so a
// demo screen shows what the module actually has to handle rather than three
// copies of the easy case.

object BusinessOpsSampleData {

    fun clients(): List<Client> = listOf(
        Client(
            clientId = "cl-nandi",
            name = "Nandi Dlamini Design",
            email = "nandi@example.co.za",
            phone = "+27 82 555 0142",
            billingAddress = "12 Long St, Cape Town, 8001",
            taxNumber = "4470112345",
            defaultCurrency = "ZAR",
            paymentTermsDays = 30,
        ),
        Client(
            clientId = "cl-thabo",
            name = "Thabo Trading CC",
            email = "accounts@thabo.example",
            phone = "+27 71 555 0199",
            billingAddress = "5 Jan Smuts Ave, Johannesburg, 2196",
            taxNumber = "4990556677",
            defaultCurrency = "ZAR",
            paymentTermsDays = 14,
        ),
        Client(
            clientId = "cl-amara",
            name = "Amara Studios (Lagos)",
            email = "hello@amara.example",
            phone = "+234 802 555 0101",
            billingAddress = "3 Awolowo Rd, Ikoyi, Lagos",
            taxNumber = null,
            defaultCurrency = "NGN",
            paymentTermsDays = 30,
        ),
    )

    fun sampleInvoice(
        invoiceId: String = "inv-sample-1",
        clientId: String = "cl-nandi",
        currency: String = "ZAR",
    ): BusinessInvoice {
        val issue = LocalDate.of(2026, 7, 1)
        val stamp = Instant.ofEpochSecond(1_782_896_400L) // 2026-07-01T09:00:00Z
        val lines = listOf(
            BusinessInvoiceLine(
                description = "Brand identity - logo suite",
                quantity = BigDecimal.ONE,
                unitPrice = Money.of(8500L, currency)!!,
                taxRate = BigDecimal("0.15"),
            ),
            BusinessInvoiceLine(
                description = "Business cards - design",
                quantity = BigDecimal(2),
                unitPrice = Money.of(750L, currency)!!,
                taxRate = BigDecimal("0.15"),
            ),
        )
        return BusinessInvoice(
            invoiceId = invoiceId,
            number = "INV-2026-0001",
            clientId = clientId,
            currency = currency,
            lines = lines,
            status = InvoiceStatus.SENT,
            issueDate = issue,
            dueDate = issue.plusDays(30),
            amountPaid = Money.zero(currency),
            notes = "Thank you for your business. Banking details overleaf.",
            createdAtUtc = stamp,
            updatedAtUtc = stamp,
        )
    }

    fun reminders(): List<Reminder> {
        val created = Instant.ofEpochSecond(1_782_896_400L) // 2026-07-01T09:00:00Z
        return listOf(
            Reminder(
                reminderId = "rem-chase-inv1",
                title = "Follow up on INV-2026-0001",
                dueAtUtc = Instant.ofEpochSecond(1_784_534_400L), // 2026-07-20T08:00:00Z
                kind = ReminderKind.INVOICE_DUE,
                relatedEntityId = "inv-sample-1",
                createdAtUtc = created,
            ),
            Reminder(
                reminderId = "rem-checkin-thabo",
                title = "Monthly check-in call",
                dueAtUtc = Instant.ofEpochSecond(1_785_571_200L), // 2026-08-01T08:00:00Z
                repeatRule = RecurrenceRule(Recurrence.MONTHLY),
                kind = ReminderKind.FOLLOW_UP,
                relatedEntityId = "cl-thabo",
                createdAtUtc = created,
            ),
        )
    }
}
