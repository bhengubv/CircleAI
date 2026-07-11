// CommerceAccounting.swift
//
// Port of the Commerce.Accounting vertical from
// src/CircleAI.Commerce.Accounting/AccountingPrimitives.cs and the static
// domain-context constants from CommerceAccountingDomainContext.cs:
//   • AccountingEntry, TaxRate, Period — domain records
//   • IAccountingBoard                 — double-entry postings / tax / balances
//   • InMemoryAccountingBoard          — deterministic in-memory impl
//   • CommerceAccountingDomainContext  — system-prompt snippet + flags
//
// The Companion-facing wrapper (CommerceAccountingCompanionAdapter) is
// intentionally NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTime` → `Date`. `Period` matches on the
//     calendar year+month of `atUtc`; the port extracts those components using a
//     UTC (`en_US_POSIX`) Gregorian calendar so behaviour is locale-independent.
//   • `Post` rejects negative debit/credit amounts (`ArgumentException`) →
//     `AccountingError.negativeAmount`.
//   • Balance / Sum use signed `debit - credit`. `ForAccount` orders ascending
//     by `atUtc` (stable). `NetProfit` = Sum(revenue) − Sum(expense).

import Foundation

// MARK: - Records

/// A single accounting ledger entry (one side may be zero).
public struct AccountingEntry: Sendable, Equatable, Codable {
    /// Stable identifier for the entry.
    public let entryId: String
    /// UTC timestamp used for period bucketing.
    public let atUtc: Date
    /// Chart-of-accounts code the entry posts to.
    public let accountCode: String
    /// Debit amount (non-negative).
    public let debitAmount: Decimal
    /// Credit amount (non-negative).
    public let creditAmount: Decimal
    /// Free-form memo.
    public let memo: String

    public init(entryId: String, atUtc: Date, accountCode: String,
                debitAmount: Decimal, creditAmount: Decimal, memo: String) {
        self.entryId = entryId
        self.atUtc = atUtc
        self.accountCode = accountCode
        self.debitAmount = debitAmount
        self.creditAmount = creditAmount
        self.memo = memo
    }
}

/// A named tax rate.
public struct TaxRate: Sendable, Equatable, Codable {
    /// Tax code (e.g. "VAT").
    public let code: String
    /// Rate as a percentage (e.g. 15.0).
    public let percentage: Double

    public init(code: String, percentage: Double) {
        self.code = code
        self.percentage = percentage
    }
}

/// A calendar reporting period.
public struct Period: Sendable, Equatable, Hashable, Codable {
    /// Four-digit year.
    public let year: Int
    /// Month 1–12.
    public let month: Int

    public init(year: Int, month: Int) {
        self.year = year
        self.month = month
    }
}

// MARK: - Errors

/// Errors thrown by the accounting board.
public enum AccountingError: Error, Equatable, CustomStringConvertible {
    /// `post` received a negative debit or credit amount.
    case negativeAmount

    public var description: String {
        switch self {
        case .negativeAmount: return "amounts must be non-negative"
        }
    }
}

// MARK: - IAccountingBoard

/// Double-entry postings, tax-rate definitions, and period reporting for the
/// commerce-accounting vertical. A synchronous contract — implementations are
/// expected to be thread-safe.
public protocol IAccountingBoard: AnyObject, Sendable {
    /// Posts an entry. Throws when either amount is negative.
    func post(_ e: AccountingEntry) throws
    /// Defines (or replaces, by `code`) a tax rate.
    func defineTax(_ r: TaxRate)
    /// Returns the tax rate with `code`, or `nil`.
    func getTax(_ code: String) -> TaxRate?
    /// Net signed balance (debits − credits) for `accountCode` across all time.
    func accountBalance(_ accountCode: String) -> Decimal
    /// Net signed balance for `accountCode` within period `p`.
    func sum(_ accountCode: String, _ p: Period) -> Decimal
    /// Entries for `accountCode` within period `p`, ascending by time.
    func forAccount(_ accountCode: String, _ p: Period) -> [AccountingEntry]
    /// Net profit for period `p` = Sum(revenueAccount) − Sum(expenseAccount).
    func netProfit(_ p: Period, revenueAccount: String, expenseAccount: String) -> Decimal
}

// MARK: - InMemoryAccountingBoard

/// Deterministic in-memory `IAccountingBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryAccountingBoard: IAccountingBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var entries: [AccountingEntry] = []
    private var tax: [String: TaxRate] = [:]

    /// UTC Gregorian calendar for locale-independent year/month extraction.
    private static let utcCalendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        c.locale = Locale(identifier: "en_US_POSIX")
        return c
    }()

    public init() {}

    private static func inPeriod(_ date: Date, _ p: Period) -> Bool {
        let comps = utcCalendar.dateComponents([.year, .month], from: date)
        return comps.year == p.year && comps.month == p.month
    }

    public func post(_ e: AccountingEntry) throws {
        if e.debitAmount < 0 || e.creditAmount < 0 { throw AccountingError.negativeAmount }
        lock.lock(); defer { lock.unlock() }
        entries.append(e)
    }

    public func defineTax(_ r: TaxRate) {
        lock.lock(); defer { lock.unlock() }
        tax[r.code] = r
    }

    public func getTax(_ code: String) -> TaxRate? {
        lock.lock(); defer { lock.unlock() }
        return tax[code]
    }

    public func accountBalance(_ accountCode: String) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return entries.filter { $0.accountCode == accountCode }
            .reduce(Decimal(0)) { $0 + ($1.debitAmount - $1.creditAmount) }
    }

    public func sum(_ accountCode: String, _ p: Period) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return entries.filter { $0.accountCode == accountCode && Self.inPeriod($0.atUtc, p) }
            .reduce(Decimal(0)) { $0 + ($1.debitAmount - $1.creditAmount) }
    }

    public func forAccount(_ accountCode: String, _ p: Period) -> [AccountingEntry] {
        lock.lock(); defer { lock.unlock() }
        return entries.filter { $0.accountCode == accountCode && Self.inPeriod($0.atUtc, p) }
            .sorted { $0.atUtc < $1.atUtc }
    }

    public func netProfit(_ p: Period, revenueAccount: String, expenseAccount: String) -> Decimal {
        sum(revenueAccount, p) - sum(expenseAccount, p)
    }
}

// MARK: - CommerceAccountingDomainContext

/// Static domain-context constants for the commerce-accounting vertical. Mirrors
/// `CommerceAccountingDomainContext` in CommerceAccountingDomainContext.cs.
public enum CommerceAccountingDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Commerce.Accounting] You are an expert accounting assistant. Help with bookkeeping, bank reconciliation, VAT calculations (SA 15% standard rate), financial statement preparation, cash flow analysis, and audit trail documentation. Cite relevant IFRS or GAAP standards. Compliance: Companies Act 71 of 2008, SARS regulations, IFRS for SMEs."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["IFRS", "SARS", "Companies_Act_71_2008", "VAT_Act"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["accounting_software", "spreadsheet", "document_editor"]
}
