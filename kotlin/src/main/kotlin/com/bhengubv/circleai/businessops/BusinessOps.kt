// BusinessOps.kt
//
// Kotlin port of CircleAI.BusinessOps — the C# reference is the EXACT spec.
//
// Running a small business from the phone: clients, invoices, reminders.
//
// Fidelity notes:
//   * C# `decimal` -> `java.math.BigDecimal`. NOT Double: 0.1 + 0.2 must be 0.3
//     on an invoice, and a binary float cannot promise that.
//   * C# `DateOnly` -> `java.time.LocalDate`. An invoice due date must not
//     carry a time or a zone: due on the 30th is the same day in Cape Town and
//     in Lagos.
//   * Rounding is HALF_UP, which is what invoices and tax authorities expect -
//     NOT HALF_EVEN, which is BigDecimal default.

package com.bhengubv.circleai.businessops

import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Instant
import java.time.LocalDate
import java.util.UUID

class MixedCurrencyException(a: String, b: String) : IllegalArgumentException(
    "Cannot combine " + (if (a.isEmpty()) "<none>" else a) + " with " +
        (if (b.isEmpty()) "<none>" else b) + ". Convert to one currency first."
)

/**
 * An amount and the currency it is in - never one without the other.
 *
 * Backed by BigDecimal, not Double: 0.1 + 0.2 must be 0.3 on an invoice.
 */
data class Money private constructor(val amount: BigDecimal, val currency: String) {

    val isZero: Boolean get() = amount.compareTo(BigDecimal.ZERO) == 0

    /** Adding rand to naira is a BUG, not a conversion - it throws. */
    operator fun plus(other: Money): Money {
        if (currency != other.currency) throw MixedCurrencyException(currency, other.currency)
        return Money(amount.add(other.amount), currency)
    }

    operator fun minus(other: Money): Money {
        if (currency != other.currency) throw MixedCurrencyException(currency, other.currency)
        return Money(amount.subtract(other.amount), currency)
    }

    operator fun times(factor: BigDecimal): Money = Money(amount.multiply(factor), currency)

    /**
     * HALF AWAY FROM ZERO, which is what invoices and tax authorities expect -
     * NOT bankers rounding, which is what BigDecimal does by default.
     */
    fun rounded(decimals: Int = 2): Money =
        Money(amount.setScale(decimals, RoundingMode.HALF_UP), currency)

    override fun toString(): String = Currencies.format(this)

    companion object {
        /**
         * Fails rather than defaulting: an amount with a GUESSED currency is
         * worse than no amount at all.
         */
        fun of(amount: BigDecimal, currency: String): Money? {
            val c = currency.trim().uppercase()
            return if (c.isEmpty()) null else Money(amount, c)
        }

        fun of(amount: Long, currency: String): Money? = of(BigDecimal.valueOf(amount), currency)

        fun zero(currency: String): Money =
            of(BigDecimal.ZERO, currency) ?: Money(BigDecimal.ZERO, "")

        /** For internal arithmetic where the currency is already known-good. */
        internal fun unchecked(amount: BigDecimal, currency: String) = Money(amount, currency)
    }
}

/** Currency codes and how to print them. */
object Currencies {
    const val DEFAULT_CURRENCY = "ZAR"

    private val symbols = mapOf(
        "ZAR" to "R", "USD" to "$", "EUR" to "€", "GBP" to "£",
        "NGN" to "₦", "KES" to "KSh", "GHS" to "₵", "TZS" to "TSh",
        "UGX" to "USh", "ZMW" to "ZK", "BWP" to "P", "NAD" to "N$",
        "MZN" to "MT", "EGP" to "E£", "MAD" to "DH", "INR" to "₹",
    )

    /**
     * The symbol, or the CODE itself when there is no symbol for it. Never a
     * blank - an amount with no currency marker is unreadable.
     */
    fun symbol(currency: String): String {
        val code = currency.trim().uppercase()
        if (code.isEmpty()) return ""
        return symbols[code] ?: code
    }

    /**
     * Thousands separated by a SPACE, two decimals, invariant. A locale-driven
     * format here would print R 1.234,56 on a phone set to German and turn a
     * thousand rand into one.
     */
    fun format(money: Money): String {
        val rounded = money.amount.setScale(2, RoundingMode.HALF_UP)
        val plain = rounded.abs().toPlainString()
        val parts = plain.split(Char(46))
        val whole = parts[0]
        val frac = if (parts.size > 1) parts[1] else "00"

        val grouped = StringBuilder()
        for ((i, c) in whole.withIndex()) {
            if (i > 0 && (whole.length - i) % 3 == 0) grouped.append(Char(32))
            grouped.append(c)
        }
        val sign = if (rounded.signum() < 0) "-" else ""
        return (symbol(money.currency) + " " + sign + grouped + Char(46) + frac).trim()
    }
}

// ── Clients ─────────────────────────────────────────────────────────────────

/** Somebody who owes you money, and how to reach them. */
data class Client(
    val clientId: String,
    val name: String,
    val email: String? = null,
    val phone: String? = null,
    val billingAddress: String? = null,
    val taxNumber: String? = null,
    val defaultCurrency: String = Currencies.DEFAULT_CURRENCY,
    val paymentTermsDays: Int = 30,
    val notes: String? = null,
    val createdAtUtc: Instant? = null,
)

// ── Invoices ────────────────────────────────────────────────────────────────

enum class InvoiceStatus { DRAFT, SENT, PARTIALLY_PAID, PAID, OVERDUE, CANCELLED }

/**
 * One line. Every figure rounds AT THE LINE, then lines are summed - summing
 * first and rounding once puts the total a cent out from what the customer adds
 * up by hand off the printed page.
 */
data class BusinessInvoiceLine(
    val description: String,
    val quantity: BigDecimal,
    val unitPrice: Money,
    val taxRate: BigDecimal = BigDecimal.ZERO,
) {
    val lineSubtotal: Money get() = (unitPrice * quantity).rounded()
    val lineTax: Money get() = (unitPrice * quantity * taxRate).rounded()
    val lineTotal: Money get() = lineSubtotal + lineTax
}

private operator fun Money.times(other: BigDecimal): Money = this.times(other)

/**
 * An invoice, with the arithmetic DERIVED rather than stored - a stored total
 * that disagrees with its lines is the classic accounting bug.
 */
data class BusinessInvoice(
    val invoiceId: String,
    val number: String? = null,
    val clientId: String,
    val currency: String,
    val lines: List<BusinessInvoiceLine> = emptyList(),
    val status: InvoiceStatus = InvoiceStatus.DRAFT,
    val issueDate: LocalDate? = null,
    val dueDate: LocalDate? = null,
    val amountPaid: Money? = null,
    val notes: String? = null,
    val createdAtUtc: Instant = Instant.EPOCH,
    val updatedAtUtc: Instant = Instant.EPOCH,
) {
    val subtotal: Money get() = fold { it.lineSubtotal }
    val taxTotal: Money get() = fold { it.lineTax }
    val total: Money get() = fold { it.lineTotal }

    val paidToDate: Money get() = amountPaid ?: Money.zero(currency)

    val balanceDue: Money
        get() = Money.unchecked(total.amount.subtract(paidToDate.amount), currency).rounded()

    val isSettled: Boolean get() = balanceDue.amount.signum() <= 0

    /** A DRAFT is not late - it was never sent. Nor is a cancelled one. */
    fun isOverdue(asOf: LocalDate): Boolean =
        !isSettled && status != InvoiceStatus.CANCELLED && status != InvoiceStatus.DRAFT &&
            dueDate != null && asOf.isAfter(dueDate)

    private fun fold(selector: (BusinessInvoiceLine) -> Money): Money {
        var acc = BigDecimal.ZERO
        for (line in lines) acc = acc.add(selector(line).amount)
        return Money.unchecked(acc, currency).rounded()
    }
}

/** Numbering has to be sequential and gapless per year - an auditor asks about gaps. */
class SequentialInvoiceNumberGenerator(
    private val prefix: String = "INV-",
    private val year: Int = LocalDate.now().year,
    seed: Long = 0,
) : IInvoiceNumberGenerator {
    private val lock = Any()
    private var seq: Long = seed

    override fun next(): String {
        val n = synchronized(lock) { ++seq }
        return prefix + year + "-" + String.format("%04d", n)
    }
}

// ── Reminders ───────────────────────────────────────────────────────────────

enum class Recurrence { NONE, DAILY, WEEKLY, MONTHLY, YEARLY }

enum class ReminderKind {
    GENERAL, FOLLOW_UP, INVOICE_DUE, CUSTOM;

    val displayName: String get() = when (this) {
        GENERAL -> "General"; FOLLOW_UP -> "FollowUp"
        INVOICE_DUE -> "InvoiceDue"; CUSTOM -> "Custom"
    }
}

/**
 * How often it comes back. MONTHLY steps by calendar month, not by 30 days - a
 * monthly check-in on the 31st must not drift to the 30th, the 29th, ...
 */
data class RecurrenceRule(val kind: Recurrence, val interval: Int = 1) {
    val isRecurring: Boolean get() = kind != Recurrence.NONE

    fun next(from: Instant): Instant? {
        val step = if (interval <= 0) 1 else interval
        val zone = java.time.ZoneOffset.UTC
        val dt = java.time.LocalDateTime.ofInstant(from, zone)
        return when (kind) {
            Recurrence.DAILY -> dt.plusDays(step.toLong()).toInstant(zone)
            Recurrence.WEEKLY -> dt.plusDays(7L * step).toInstant(zone)
            Recurrence.MONTHLY -> dt.plusMonths(step.toLong()).toInstant(zone)
            Recurrence.YEARLY -> dt.plusYears(step.toLong()).toInstant(zone)
            Recurrence.NONE -> null
        }
    }

    companion object {
        val ONCE = RecurrenceRule(Recurrence.NONE, 0)
    }
}

data class Reminder(
    val reminderId: String,
    val title: String,
    val dueAtUtc: Instant,
    val repeatRule: RecurrenceRule = RecurrenceRule.ONCE,
    val kind: ReminderKind = ReminderKind.GENERAL,
    val relatedEntityId: String? = null,
    val completed: Boolean = false,
    val notes: String? = null,
    val createdAtUtc: Instant? = null,
) {
    fun isDue(asOf: Instant): Boolean = !completed && !asOf.isBefore(dueAtUtc)
}
