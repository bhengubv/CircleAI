// PersonalFinance.swift
//
// Port of the Personal.Finance vertical from
// src/CircleAI.Personal.Finance/PersonalFinancePrimitives.cs and the static
// domain-context constants from PersonalFinanceDomainContext.cs:
//   • FinanceAccount, FinanceTransaction, BudgetLine, MonthSummary — records
//   • IPersonalFinanceBoard   — accounts / transactions / budgets / summary
//   • InMemoryPersonalFinanceBoard — deterministic in-memory impl
//   • PersonalFinanceDomainContext — system-prompt snippet + flags
//
// The Companion-facing wrapper (PersonalFinanceCompanionAdapter) is intentionally
// NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • The C# record `Account` is renamed `FinanceAccount` so it does not collide
//     with `BankAccount` (Banking) in the flat Swift module.
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`; `string? Note` → `String?`.
//   • `Record` on an unknown account throws → `PersonalFinanceError.unknownAccount`,
//     and (as in C#) adjusts the account balance by the transaction amount.
//   • Budgets are keyed case-insensitively (C# `StringComparer.OrdinalIgnoreCase`);
//     `Budgets` returns them ordered ascending by category.
//   • `ListForMonth` filters by account + calendar year/month of `atUtc`
//     (extracted with a UTC calendar for locale independence).
//   • `Summarise` buckets by category, `totalIn` = Σ positive amounts,
//     `totalOut` = −Σ negative amounts.

import Foundation

// MARK: - Records

/// A personal-finance account. (C# `Account` in CircleAI.Personal.Finance.)
public struct FinanceAccount: Sendable, Equatable, Codable {
    /// Stable identifier for the account.
    public let accountId: String
    /// Account name.
    public let name: String
    /// Current balance.
    public let balance: Decimal
    /// ISO currency code.
    public let currency: String

    public init(accountId: String, name: String, balance: Decimal, currency: String) {
        self.accountId = accountId
        self.name = name
        self.balance = balance
        self.currency = currency
    }
}

/// A personal-finance transaction (signed amount).
public struct FinanceTransaction: Sendable, Equatable, Codable {
    /// Stable identifier for the transaction.
    public let txId: String
    /// Identifier of the account the transaction posts to.
    public let accountId: String
    /// Signed amount (positive = money in, negative = money out).
    public let amount: Decimal
    /// Category (e.g. "groceries").
    public let category: String
    /// Optional note.
    public let note: String?
    /// UTC timestamp.
    public let atUtc: Date

    public init(txId: String, accountId: String, amount: Decimal, category: String,
                note: String?, atUtc: Date) {
        self.txId = txId
        self.accountId = accountId
        self.amount = amount
        self.category = category
        self.note = note
        self.atUtc = atUtc
    }
}

/// A monthly budget limit for a category.
public struct BudgetLine: Sendable, Equatable, Codable {
    /// Category the budget applies to.
    public let category: String
    /// Monthly spending limit.
    public let monthlyLimit: Decimal

    public init(category: String, monthlyLimit: Decimal) {
        self.category = category
        self.monthlyLimit = monthlyLimit
    }
}

/// A one-month roll-up of activity for an account.
public struct MonthSummary: Sendable, Equatable, Codable {
    /// Four-digit year.
    public let year: Int
    /// Month 1–12.
    public let month: Int
    /// Total money in (sum of positive amounts).
    public let totalIn: Decimal
    /// Total money out (absolute value of the sum of negative amounts).
    public let totalOut: Decimal
    /// Net amount per category.
    public let byCategory: [String: Decimal]

    public init(year: Int, month: Int, totalIn: Decimal, totalOut: Decimal, byCategory: [String: Decimal]) {
        self.year = year
        self.month = month
        self.totalIn = totalIn
        self.totalOut = totalOut
        self.byCategory = byCategory
    }
}

// MARK: - Errors

/// Errors thrown by the personal-finance board.
public enum PersonalFinanceError: Error, Equatable, CustomStringConvertible {
    /// `record` targeted an account id that is not known.
    case unknownAccount(String)

    public var description: String {
        switch self {
        case .unknownAccount(let id): return "Unknown account \(id)"
        }
    }
}

// MARK: - IPersonalFinanceBoard

/// Accounts, transactions, budgets, and monthly summaries for the
/// personal-finance vertical. A synchronous contract — implementations are
/// expected to be thread-safe.
public protocol IPersonalFinanceBoard: AnyObject, Sendable {
    /// Inserts (or replaces, by `accountId`) an account.
    func upsert(_ a: FinanceAccount)
    /// Returns the account with `id`, or `nil`.
    func getAccount(_ id: String) -> FinanceAccount?
    /// Records a transaction, adjusting the account balance. Throws when unknown.
    func record(_ t: FinanceTransaction) throws
    /// Transactions for `accountId` in the given calendar month.
    func listForMonth(accountId: String, year: Int, month: Int) -> [FinanceTransaction]
    /// Sets (or replaces, case-insensitively by category) a budget line.
    func setBudget(_ b: BudgetLine)
    /// Budget lines ordered ascending by category.
    var budgets: [BudgetLine] { get }
    /// Summarises `accountId` for the given calendar month.
    func summarise(accountId: String, year: Int, month: Int) -> MonthSummary
}

// MARK: - InMemoryPersonalFinanceBoard

/// Deterministic in-memory `IPersonalFinanceBoard`. All state is guarded by a
/// single `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryPersonalFinanceBoard: IPersonalFinanceBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var accounts: [String: FinanceAccount] = [:]
    // Budget keys are lower-cased for case-insensitive replacement; the original
    // category casing is preserved inside the stored BudgetLine.
    private var budgetsByKey: [String: BudgetLine] = [:]
    private var txns: [FinanceTransaction] = []

    private static let utcCalendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        c.locale = Locale(identifier: "en_US_POSIX")
        return c
    }()

    public init() {}

    public func upsert(_ a: FinanceAccount) {
        lock.lock(); defer { lock.unlock() }
        accounts[a.accountId] = a
    }

    public func getAccount(_ id: String) -> FinanceAccount? {
        lock.lock(); defer { lock.unlock() }
        return accounts[id]
    }

    public func record(_ t: FinanceTransaction) throws {
        lock.lock(); defer { lock.unlock() }
        guard let a = accounts[t.accountId] else { throw PersonalFinanceError.unknownAccount(t.accountId) }
        txns.append(t)
        accounts[t.accountId] = FinanceAccount(accountId: a.accountId, name: a.name,
                                               balance: a.balance + t.amount, currency: a.currency)
    }

    public func listForMonth(accountId: String, year: Int, month: Int) -> [FinanceTransaction] {
        lock.lock(); defer { lock.unlock() }
        return txns.filter {
            guard $0.accountId == accountId else { return false }
            let c = Self.utcCalendar.dateComponents([.year, .month], from: $0.atUtc)
            return c.year == year && c.month == month
        }
    }

    public func setBudget(_ b: BudgetLine) {
        lock.lock(); defer { lock.unlock() }
        budgetsByKey[b.category.lowercased()] = b
    }

    public var budgets: [BudgetLine] {
        lock.lock(); defer { lock.unlock() }
        return budgetsByKey.values.sorted { $0.category < $1.category }
    }

    public func summarise(accountId: String, year: Int, month: Int) -> MonthSummary {
        let rows = listForMonth(accountId: accountId, year: year, month: month)
        var byCat: [String: Decimal] = [:]
        for t in rows { byCat[t.category, default: 0] += t.amount }
        let inSum = rows.filter { $0.amount > 0 }.reduce(Decimal(0)) { $0 + $1.amount }
        let outSum = -rows.filter { $0.amount < 0 }.reduce(Decimal(0)) { $0 + $1.amount }
        return MonthSummary(year: year, month: month, totalIn: inSum, totalOut: outSum, byCategory: byCat)
    }
}

// MARK: - PersonalFinanceDomainContext

/// Static domain-context constants for the personal-finance vertical. Mirrors
/// `PersonalFinanceDomainContext` in PersonalFinanceDomainContext.cs.
public enum PersonalFinanceDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Personal.Finance] Personal finance coach. Help with monthly budgeting, emergency fund planning, debt snowball/avalanche strategy, savings goals, retirement planning basics, and investment options education. IMPORTANT: This is financial education, not advice. Recommend a registered financial planner for personalised investment advice. Compliance: FAIS Act, NCA, POPIA."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["budget_tracker", "spreadsheet", "calculator", "web_search"]
}
