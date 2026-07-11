// CommerceFinance.swift
//
// Port of the Commerce.Finance vertical from
// src/CircleAI.Commerce.Finance/FinancePrimitives.cs and the static
// domain-context constants from CommerceFinanceDomainContext.cs:
//   • InvoiceLine, Invoice, FinancePayment — domain records
//   • IInvoiceBoard                        — issue / pay / overdue / outstanding
//   • InMemoryInvoiceBoard                 — deterministic in-memory impl
//   • CommerceFinanceDomainContext         — system-prompt snippet + flags
//
// The Companion-facing wrapper (CommerceFinanceCompanionAdapter) is intentionally
// NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `decimal` → `Decimal`; `double TaxPct` → `Double`; `DateTime` → `Date`.
//   • `RemainingOn` reproduces the C# mixed-arithmetic exactly:
//         billed = Σ line.Amount * (decimal)(1 + line.TaxPct / 100.0)
//     i.e. the tax factor `1 + taxPct/100` is computed in Double, cast to
//     Decimal, then multiplied by the Decimal `amount`. `remainingOn` returns 0
//     for an unknown invoice.
//   • `MarkOverdue` flips any invoice past due and not "Paid" (case-insensitive)
//     to "Overdue". `Overdue()` returns invoices whose status is "Overdue"
//     (case-insensitive). `TotalOutstanding` sums `remainingOn` over all invoices.

import Foundation

// MARK: - Records

/// A single line on an invoice.
public struct InvoiceLine: Sendable, Equatable, Codable {
    /// Line description.
    public let description: String
    /// Pre-tax amount.
    public let amount: Decimal
    /// Tax percentage applied to this line (e.g. 15.0).
    public let taxPct: Double

    public init(description: String, amount: Decimal, taxPct: Double) {
        self.description = description
        self.amount = amount
        self.taxPct = taxPct
    }
}

/// A customer invoice.
public struct Invoice: Sendable, Equatable, Codable {
    /// Stable identifier for the invoice.
    public let invoiceId: String
    /// Identifier of the billed customer.
    public let customerId: String
    /// Issue date.
    public let issueDate: Date
    /// Due date.
    public let dueDate: Date
    /// Invoice lines.
    public let lines: [InvoiceLine]
    /// ISO currency code.
    public let currency: String
    /// Free-form status (e.g. "Issued", "Paid", "Overdue").
    public let status: String

    public init(invoiceId: String, customerId: String, issueDate: Date, dueDate: Date,
                lines: [InvoiceLine], currency: String, status: String) {
        self.invoiceId = invoiceId
        self.customerId = customerId
        self.issueDate = issueDate
        self.dueDate = dueDate
        self.lines = lines
        self.currency = currency
        self.status = status
    }
}

/// A payment recorded against an invoice.
public struct FinancePayment: Sendable, Equatable, Codable {
    /// Stable identifier for the payment.
    public let paymentId: String
    /// Identifier of the invoice paid.
    public let invoiceId: String
    /// Amount paid.
    public let amount: Decimal
    /// UTC timestamp.
    public let atUtc: Date

    public init(paymentId: String, invoiceId: String, amount: Decimal, atUtc: Date) {
        self.paymentId = paymentId
        self.invoiceId = invoiceId
        self.amount = amount
        self.atUtc = atUtc
    }
}

// MARK: - IInvoiceBoard

/// Invoices, payments, and overdue tracking for the commerce-finance vertical.
/// A synchronous contract — implementations are expected to be thread-safe.
public protocol IInvoiceBoard: AnyObject, Sendable {
    /// Issues (or replaces, by `invoiceId`) an invoice.
    func issue(_ i: Invoice)
    /// Returns the invoice with `invoiceId`, or `nil`.
    func get(_ invoiceId: String) -> Invoice?
    /// Records a payment against an invoice.
    func recordPayment(_ p: FinancePayment)
    /// Flips overdue, unpaid invoices to "Overdue" as of `asOf`.
    func markOverdue(_ asOf: Date)
    /// Billed-minus-paid remaining on `invoiceId` (0 if unknown).
    func remainingOn(_ invoiceId: String) -> Decimal
    /// Sum of `remainingOn` across all invoices.
    func totalOutstanding() -> Decimal
    /// Invoices currently marked "Overdue".
    func overdue() -> [Invoice]
}

// MARK: - InMemoryInvoiceBoard

/// Deterministic in-memory `IInvoiceBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryInvoiceBoard: IInvoiceBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var invoices: [String: Invoice] = [:]
    private var payments: [FinancePayment] = []

    public init() {}

    public func issue(_ i: Invoice) {
        lock.lock(); defer { lock.unlock() }
        invoices[i.invoiceId] = i
    }

    public func get(_ invoiceId: String) -> Invoice? {
        lock.lock(); defer { lock.unlock() }
        return invoices[invoiceId]
    }

    public func recordPayment(_ p: FinancePayment) {
        lock.lock(); defer { lock.unlock() }
        payments.append(p)
    }

    public func markOverdue(_ asOf: Date) {
        lock.lock(); defer { lock.unlock() }
        for i in invoices.values where i.dueDate < asOf && i.status.caseInsensitiveCompare("Paid") != .orderedSame {
            invoices[i.invoiceId] = Invoice(invoiceId: i.invoiceId, customerId: i.customerId,
                                            issueDate: i.issueDate, dueDate: i.dueDate, lines: i.lines,
                                            currency: i.currency, status: "Overdue")
        }
    }

    public func remainingOn(_ invoiceId: String) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return remainingLocked(invoiceId)
    }

    /// Non-reentrant remaining computation; caller must hold `lock`.
    private func remainingLocked(_ invoiceId: String) -> Decimal {
        guard let inv = invoices[invoiceId] else { return 0 }
        let billed = inv.lines.reduce(Decimal(0)) { acc, l in
            // Mirror C#: (decimal)(1 + l.TaxPct / 100.0) computed in Double, cast.
            let factor = Decimal(1 + l.taxPct / 100.0)
            return acc + l.amount * factor
        }
        let paid = payments.filter { $0.invoiceId == invoiceId }.reduce(Decimal(0)) { $0 + $1.amount }
        return billed - paid
    }

    public func totalOutstanding() -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return invoices.keys.reduce(Decimal(0)) { $0 + remainingLocked($1) }
    }

    public func overdue() -> [Invoice] {
        lock.lock(); defer { lock.unlock() }
        return invoices.values.filter { $0.status.caseInsensitiveCompare("Overdue") == .orderedSame }
    }
}

// MARK: - CommerceFinanceDomainContext

/// Static domain-context constants for the commerce-finance vertical. Mirrors
/// `CommerceFinanceDomainContext` in CommerceFinanceDomainContext.cs.
public enum CommerceFinanceDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Commerce.Finance] You are a commercial finance expert. Help with working capital optimisation, cash flow forecasting, business credit applications, debt structuring, and treasury policy. Ground advice in the cash conversion cycle and credit profile. Compliance: NCA (National Credit Act 34 of 2005), SARB prudential rules, POPIA."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["NCA_34_2005", "SARB_aware", "POPIA", "IFRS"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["cash_flow_model", "spreadsheet", "web_search"]
}
