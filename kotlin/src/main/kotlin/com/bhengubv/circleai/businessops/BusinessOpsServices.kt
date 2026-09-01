// BusinessOpsServices.kt
//
// The service half of CircleAI.BusinessOps: the storage contracts, the
// in-memory store, the client book, the invoice service, the reminder
// scheduler, the CRM bridge and the sample data.
//
// The value types live in BusinessOps.kt. Everything here is a policy on top of
// them, and every policy that can refuse does so with a named BusinessOpsError
// rather than a bare IllegalStateException.

package com.bhengubv.circleai.businessops

import com.bhengubv.circleai.crm.Activity
import com.bhengubv.circleai.crm.Contact
import com.bhengubv.circleai.crm.IContactStore
import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.util.UUID

// ------------------------------------------------------------- Errors

/**
 * Every refusal this module can make, named.
 *
 * The alternative - a bare IllegalStateException with a message - reads the
 * same at the throw site and is useless at the catch site, which is where a
 * screen has to decide between "tell them to pick another invoice" and "tell
 * them the payment was in the wrong currency".
 */
sealed class BusinessOpsError(message: String) : Exception(message) {

    class InvoiceNotFound(val invoiceId: String) :
        BusinessOpsError("Invoice " + invoiceId + " not found.")

    class ReminderNotFound(val reminderId: String) :
        BusinessOpsError("Reminder " + reminderId + " not found.")

    class CancelledCannotBeIssued :
        BusinessOpsError("A cancelled invoice cannot be issued.")

    class CancelledCannotBePaid :
        BusinessOpsError("Cannot record a payment against a cancelled invoice.")

    class PaidCannotBeCancelled :
        BusinessOpsError("A paid invoice cannot be cancelled; issue a credit note instead.")

    class LineCurrencyMismatch(
        val line: String,
        val lineCurrency: String,
        val invoiceCurrency: String,
    ) : BusinessOpsError(
        "Line " + line + " is priced in " + lineCurrency +
            " but the invoice is " + invoiceCurrency + ".",
    )

    class PaymentCurrencyMismatch(val payment: String, val invoice: String) :
        BusinessOpsError(
            "Payment currency " + payment + " does not match invoice currency " + invoice + ".",
        )

    class PaymentMustBePositive :
        BusinessOpsError("A payment must be a positive amount.")

    class MissingField(val field: String) : BusinessOpsError(field + " is required.")

    class NoPdfRenderer : BusinessOpsError(
        "No invoice PDF renderer is configured. " +
            "Wire a Documents-backed renderer at the host layer.",
    )
}

internal fun requireField(value: String?, name: String): String {
    if (value == null || value.isBlank()) throw BusinessOpsError.MissingField(name)
    return value
}

// ---------------------------------------------------------- Contracts

/** The client book. */
interface IClientBook {
    val backendId: String
    suspend fun upsert(client: Client): Client
    suspend fun get(clientId: String): Client?
    suspend fun search(query: String, topK: Int = 20): List<Client>
    suspend fun list(): List<Client>
    suspend fun remove(clientId: String): Boolean
}

/** Invoice lifecycle: draft, issue, pay, cancel, and the lists a screen needs. */
interface IInvoiceService {
    val backendId: String

    suspend fun createDraft(
        clientId: String,
        currency: String,
        lines: List<BusinessInvoiceLine>,
        issueDate: LocalDate,
        paymentTermsDays: Int? = null,
        notes: String? = null,
    ): BusinessInvoice

    suspend fun get(invoiceId: String): BusinessInvoice?

    suspend fun issue(
        invoiceId: String,
        issueDate: LocalDate? = null,
        paymentTermsDays: Int = 30,
    ): BusinessInvoice

    suspend fun recordPayment(invoiceId: String, amount: Money): BusinessInvoice
    suspend fun markPaid(invoiceId: String): BusinessInvoice
    suspend fun cancel(invoiceId: String): BusinessInvoice
    suspend fun list(status: InvoiceStatus? = null): List<BusinessInvoice>
    suspend fun listByClient(clientId: String): List<BusinessInvoice>
    suspend fun listOverdue(asOf: LocalDate): List<BusinessInvoice>
    suspend fun refreshOverdue(asOf: LocalDate): Int
}

interface IInvoiceNumberGenerator {
    fun next(): String
}

interface IInvoicePdfRenderer {
    val backendId: String
    suspend fun render(invoice: BusinessInvoice, client: Client?): ByteArray
}

/**
 * Refuses, loudly. A renderer that quietly produced a blank page would be
 * worse, because somebody would send it.
 */
class NullInvoicePdfRenderer : IInvoicePdfRenderer {
    override val backendId: String get() = "null"
    override suspend fun render(invoice: BusinessInvoice, client: Client?): ByteArray =
        throw BusinessOpsError.NoPdfRenderer()

    companion object { val instance = NullInvoicePdfRenderer() }
}

interface IReminderScheduler {
    val backendId: String
    suspend fun schedule(reminder: Reminder): Reminder

    suspend fun scheduleFollowUp(
        relatedEntityId: String,
        title: String,
        dueAtUtc: Instant,
        repeatRule: RecurrenceRule? = null,
    ): Reminder

    suspend fun get(reminderId: String): Reminder?

    /** Returns the NEXT occurrence for a recurring reminder, or null for a one-off. */
    suspend fun complete(reminderId: String): Reminder?

    suspend fun cancel(reminderId: String): Boolean
    suspend fun listDue(asOf: Instant): List<Reminder>
    suspend fun listPending(): List<Reminder>
    suspend fun listForEntity(relatedEntityId: String): List<Reminder>
}

// ------------------------------------------------------------ Storage

interface IClientRepository {
    suspend fun upsert(client: Client)
    suspend fun get(clientId: String): Client?
    suspend fun list(): List<Client>
    suspend fun remove(clientId: String): Boolean
}

interface IInvoiceRepository {
    suspend fun upsert(invoice: BusinessInvoice)
    suspend fun get(invoiceId: String): BusinessInvoice?
    suspend fun list(): List<BusinessInvoice>
    suspend fun remove(invoiceId: String): Boolean
}

interface IReminderRepository {
    suspend fun upsert(reminder: Reminder)
    suspend fun get(reminderId: String): Reminder?
    suspend fun list(): List<Reminder>
    suspend fun remove(reminderId: String): Boolean
}

interface IBusinessStore {
    val backendId: String
    val clients: IClientRepository
    val invoices: IInvoiceRepository
    val reminders: IReminderRepository
}

/**
 * A tiny keyed store. Everything here is keyed by a string id, so listing order
 * never depends on insertion order - the callers sort on the way out.
 */
private class KeyedStore<T> {
    private val lock = Any()
    private val items = LinkedHashMap<String, T>()

    fun put(key: String, value: T) { synchronized(lock) { items[key] = value } }
    fun fetch(key: String): T? = synchronized(lock) { items[key] }
    fun all(): List<T> = synchronized(lock) { items.values.toList() }
    fun drop(key: String): Boolean = synchronized(lock) { items.remove(key) != null }
}

class InMemoryClientRepository : IClientRepository {
    private val store = KeyedStore<Client>()
    override suspend fun upsert(client: Client) {
        requireField(client.clientId, "clientId")
        store.put(client.clientId, client)
    }
    override suspend fun get(clientId: String): Client? = store.fetch(clientId)
    override suspend fun list(): List<Client> = store.all().sortedBy { it.name.lowercase() }
    override suspend fun remove(clientId: String): Boolean = store.drop(clientId)
}

class InMemoryInvoiceRepository : IInvoiceRepository {
    private val store = KeyedStore<BusinessInvoice>()
    override suspend fun upsert(invoice: BusinessInvoice) {
        requireField(invoice.invoiceId, "invoiceId")
        store.put(invoice.invoiceId, invoice)
    }
    override suspend fun get(invoiceId: String): BusinessInvoice? = store.fetch(invoiceId)
    override suspend fun list(): List<BusinessInvoice> = store.all()
    override suspend fun remove(invoiceId: String): Boolean = store.drop(invoiceId)
}

class InMemoryReminderRepository : IReminderRepository {
    private val store = KeyedStore<Reminder>()
    override suspend fun upsert(reminder: Reminder) {
        requireField(reminder.reminderId, "reminderId")
        store.put(reminder.reminderId, reminder)
    }
    override suspend fun get(reminderId: String): Reminder? = store.fetch(reminderId)
    override suspend fun list(): List<Reminder> = store.all()
    override suspend fun remove(reminderId: String): Boolean = store.drop(reminderId)
}

class InMemoryBusinessStore : IBusinessStore {
    override val backendId: String get() = "in-memory"
    override val clients: IClientRepository = InMemoryClientRepository()
    override val invoices: IInvoiceRepository = InMemoryInvoiceRepository()
    override val reminders: IReminderRepository = InMemoryReminderRepository()
}

// -------------------------------------------------------------- Clock

/** A clock, so every date in a test is decided by the test. */
interface IBusinessClock {
    fun now(): Instant
}

class SystemBusinessClock : IBusinessClock {
    override fun now(): Instant = Instant.now()
}

class FixedBusinessClock(private val instant: Instant) : IBusinessClock {
    override fun now(): Instant = instant
}

internal object BusinessOpsIds {
    fun new(): String = UUID.randomUUID().toString().replace("-", "").lowercase()
}
