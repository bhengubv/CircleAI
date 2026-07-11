// Banking.swift
//
// Port of the Banking vertical from src/CircleAI.Banking/:
//   • Contracts.cs         — BankAccount, LedgerEntry, PaymentRequest,
//                            PaymentResult records; IAccountReader,
//                            ILedgerWriter, IPaymentProcessor contracts
//   • InMemoryBanking.cs   — InMemoryBank (double-entry engine) plus the three
//                            in-memory backend adapters
//   • NullImplementations.cs — fail-closed Null* backends
//
// Porting notes:
//   • The C# record `Account` is renamed `BankAccount` here so it does not
//     collide with `FinanceAccount` (Personal.Finance) in the flat Swift module.
//     All other names are preserved.
//   • `decimal` → `Decimal`; money math uses Decimal throughout.
//   • `Guid.NewGuid().ToString("n")` → a 32-char lower-hex UUID with dashes
//     stripped. `Guid.Empty.ToString()` → the canonical all-zero GUID string
//     `00000000-0000-0000-0000-000000000000` (used by NullPaymentProcessor).
//   • The C# `InMemoryBank` guards balances + ledger with a single `_txLock`
//     and additionally uses `ConcurrentDictionary` for the account map; here a
//     single `NSLock` guards all state. `Read`/`Append`/`ProcessPayment` run
//     under that lock. `ProcessPayment` calls the private locked appender via a
//     non-reentrant helper (`appendLocked`) to avoid deadlocking on `NSLock`.
//   • `ValueTask<T>`-returning contract methods become `async` methods returning
//     the value directly; the in-memory impls complete synchronously.

import Foundation

// MARK: - Records

/// A bank account. (C# `Account` in CircleAI.Banking.)
public struct BankAccount: Sendable, Equatable, Codable {
    /// Stable identifier for the account.
    public let accountId: String
    /// Identifier of the account owner.
    public let ownerId: String
    /// ISO currency code (e.g. "ZAR").
    public let currency: String
    /// Current balance.
    public let balance: Decimal

    public init(accountId: String, ownerId: String, currency: String, balance: Decimal) {
        self.accountId = accountId
        self.ownerId = ownerId
        self.currency = currency
        self.balance = balance
    }
}

/// A single ledger entry (signed delta) against an account.
public struct LedgerEntry: Sendable, Equatable, Codable {
    /// Transaction identifier (shared across the two legs of a transfer).
    public let txId: String
    /// Identifier of the account this entry posts to.
    public let accountId: String
    /// Signed amount (negative debits the account, positive credits it).
    public let amount: Decimal
    /// Free-form memo.
    public let memo: String
    /// UTC timestamp.
    public let atUtc: Date

    public init(txId: String, accountId: String, amount: Decimal, memo: String, atUtc: Date) {
        self.txId = txId
        self.accountId = accountId
        self.amount = amount
        self.memo = memo
        self.atUtc = atUtc
    }
}

/// A payment (transfer) request between two accounts.
public struct PaymentRequest: Sendable, Equatable, Codable {
    /// Source account id.
    public let fromAccount: String
    /// Destination account id.
    public let toAccount: String
    /// Amount to transfer (must be positive).
    public let amount: Decimal
    /// ISO currency code; must match both accounts.
    public let currency: String
    /// Free-form memo.
    public let memo: String

    public init(fromAccount: String, toAccount: String, amount: Decimal, currency: String, memo: String) {
        self.fromAccount = fromAccount
        self.toAccount = toAccount
        self.amount = amount
        self.currency = currency
        self.memo = memo
    }
}

/// The result of processing a payment.
public struct PaymentResult: Sendable, Equatable, Codable {
    /// Transaction identifier assigned to the payment (present even on failure).
    public let txId: String
    /// Whether the payment was accepted.
    public let accepted: Bool
    /// Failure reason, or `nil` on success.
    public let failureReason: String?

    public init(txId: String, accepted: Bool, failureReason: String?) {
        self.txId = txId
        self.accepted = accepted
        self.failureReason = failureReason
    }
}

// MARK: - Errors

/// Errors thrown by the in-memory bank engine. Mirrors the C#
/// `InvalidOperationException` thrown by `Append` on an unknown account.
public enum BankingError: Error, Equatable, CustomStringConvertible {
    /// `append` targeted an account id that is not seeded.
    case unknownAccount(String)

    public var description: String {
        switch self {
        case .unknownAccount(let id): return "Unknown account \(id)"
        }
    }
}

// MARK: - Contracts

/// Reads account state from a banking backend.
public protocol IAccountReader: Sendable {
    /// Identifier of the concrete backend (e.g. "in-memory", "null").
    var backendId: String { get }
    /// Returns the account with `accountId`, or `nil` if unknown.
    func getAccount(_ accountId: String) async -> BankAccount?
    /// Returns all accounts owned by `ownerId`.
    func listForOwner(_ ownerId: String) async -> [BankAccount]
}

/// Appends to and reads from an account ledger.
public protocol ILedgerWriter: Sendable {
    /// Identifier of the concrete backend.
    var backendId: String { get }
    /// Appends `entry` and returns it. Throws when the target account is unknown.
    func append(_ entry: LedgerEntry) async throws -> LedgerEntry
    /// Returns up to `limit` most-recent ledger entries for `accountId`,
    /// newest first.
    func read(_ accountId: String, limit: Int) async -> [LedgerEntry]
}

public extension ILedgerWriter {
    /// Overload matching the C# default `limit = 100`.
    func read(_ accountId: String) async -> [LedgerEntry] {
        await read(accountId, limit: 100)
    }
}

/// Processes payments between accounts.
public protocol IPaymentProcessor: Sendable {
    /// Identifier of the concrete backend.
    var backendId: String { get }
    /// Processes `req`, returning a `PaymentResult`.
    func process(_ req: PaymentRequest) async -> PaymentResult
}

// MARK: - InMemoryBank

/// Concurrent in-memory bank shared by the reader / ledger / payment adapters.
/// Implements a double-entry payment engine (debit source, credit destination)
/// with balance, currency, and positivity checks. All state is guarded by a
/// single `NSLock`.
public final class InMemoryBank: @unchecked Sendable {
    private let lock = NSLock()
    private var accounts: [String: BankAccount] = [:]
    private var ledger: [String: [LedgerEntry]] = [:]

    public init() {}

    /// Seeds (or replaces, by `accountId`) an account.
    public func seedAccount(_ account: BankAccount) {
        lock.lock(); defer { lock.unlock() }
        accounts[account.accountId] = account
    }

    /// Returns the account with `id`, or `nil`.
    public func get(_ id: String) -> BankAccount? {
        lock.lock(); defer { lock.unlock() }
        return accounts[id]
    }

    /// Returns all accounts owned by `ownerId`.
    public func listForOwner(_ ownerId: String) -> [BankAccount] {
        lock.lock(); defer { lock.unlock() }
        return accounts.values.filter { $0.ownerId == ownerId }
    }

    /// Appends `entry`, adjusting the target account's balance. Throws when the
    /// account is unknown.
    public func append(_ entry: LedgerEntry) throws -> LedgerEntry {
        lock.lock(); defer { lock.unlock() }
        return try appendLocked(entry)
    }

    /// Non-reentrant appender; the caller must already hold `lock`.
    private func appendLocked(_ entry: LedgerEntry) throws -> LedgerEntry {
        guard let acct = accounts[entry.accountId] else {
            throw BankingError.unknownAccount(entry.accountId)
        }
        accounts[entry.accountId] = BankAccount(accountId: acct.accountId, ownerId: acct.ownerId,
                                                currency: acct.currency, balance: acct.balance + entry.amount)
        ledger[entry.accountId, default: []].append(entry)
        return entry
    }

    /// Returns up to `limit` most-recent ledger entries for `accountId`,
    /// newest first.
    public func read(_ accountId: String, limit: Int) -> [LedgerEntry] {
        lock.lock(); defer { lock.unlock() }
        guard let list = ledger[accountId] else { return [] }
        return Array(list.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }

    /// Processes a double-entry payment. Returns a failure result (never throws)
    /// for business-rule violations: non-positive amount, unknown accounts,
    /// currency mismatch, or insufficient funds.
    public func processPayment(_ req: PaymentRequest) -> PaymentResult {
        if req.amount <= 0 {
            return PaymentResult(txId: Self.newGuidN(), accepted: false, failureReason: "Amount must be positive")
        }
        lock.lock(); defer { lock.unlock() }
        guard let src = accounts[req.fromAccount] else {
            return PaymentResult(txId: Self.newGuidN(), accepted: false, failureReason: "Unknown source account")
        }
        guard let dst = accounts[req.toAccount] else {
            return PaymentResult(txId: Self.newGuidN(), accepted: false, failureReason: "Unknown destination account")
        }
        if src.currency.caseInsensitiveCompare(req.currency) != .orderedSame ||
            dst.currency.caseInsensitiveCompare(req.currency) != .orderedSame {
            return PaymentResult(txId: Self.newGuidN(), accepted: false, failureReason: "Currency mismatch")
        }
        if src.balance < req.amount {
            return PaymentResult(txId: Self.newGuidN(), accepted: false, failureReason: "Insufficient funds")
        }
        let txId = Self.newGuidN()
        let now = Date()
        // Both legs post under the already-held lock via the non-reentrant
        // appender. Accounts are known + funded, so append cannot throw here;
        // `try?` keeps the money path total.
        _ = try? appendLocked(LedgerEntry(txId: txId, accountId: req.fromAccount, amount: -req.amount,
                                          memo: "To \(req.toAccount): \(req.memo)", atUtc: now))
        _ = try? appendLocked(LedgerEntry(txId: txId, accountId: req.toAccount, amount: req.amount,
                                          memo: "From \(req.fromAccount): \(req.memo)", atUtc: now))
        return PaymentResult(txId: txId, accepted: true, failureReason: nil)
    }

    /// 32-char lower-hex GUID (`Guid.NewGuid().ToString("n")`).
    static func newGuidN() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }
}

// MARK: - In-memory backends

/// In-memory `IAccountReader` over a shared `InMemoryBank`.
public final class InMemoryAccountReader: IAccountReader, @unchecked Sendable {
    private let bank: InMemoryBank
    public init(_ bank: InMemoryBank) { self.bank = bank }
    public var backendId: String { "in-memory" }
    public func getAccount(_ id: String) async -> BankAccount? { bank.get(id) }
    public func listForOwner(_ owner: String) async -> [BankAccount] { bank.listForOwner(owner) }
}

/// In-memory `ILedgerWriter` over a shared `InMemoryBank`.
public final class InMemoryLedgerWriter: ILedgerWriter, @unchecked Sendable {
    private let bank: InMemoryBank
    public init(_ bank: InMemoryBank) { self.bank = bank }
    public var backendId: String { "in-memory" }
    public func append(_ e: LedgerEntry) async throws -> LedgerEntry { try bank.append(e) }
    public func read(_ acc: String, limit: Int) async -> [LedgerEntry] { bank.read(acc, limit: limit) }
}

/// In-memory `IPaymentProcessor` over a shared `InMemoryBank`.
public final class InMemoryPaymentProcessor: IPaymentProcessor, @unchecked Sendable {
    private let bank: InMemoryBank
    public init(_ bank: InMemoryBank) { self.bank = bank }
    public var backendId: String { "in-memory" }
    public func process(_ req: PaymentRequest) async -> PaymentResult { bank.processPayment(req) }
}

// MARK: - Null (fail-closed) backends

/// Fail-closed `IAccountReader`: knows no accounts.
public final class NullAccountReader: IAccountReader, @unchecked Sendable {
    public static let instance = NullAccountReader()
    public init() {}
    public var backendId: String { "null" }
    public func getAccount(_ id: String) async -> BankAccount? { nil }
    public func listForOwner(_ owner: String) async -> [BankAccount] { [] }
}

/// Fail-closed `ILedgerWriter`: echoes appends, reads nothing.
public final class NullLedgerWriter: ILedgerWriter, @unchecked Sendable {
    public static let instance = NullLedgerWriter()
    public init() {}
    public var backendId: String { "null" }
    public func append(_ e: LedgerEntry) async throws -> LedgerEntry { e }
    public func read(_ acc: String, limit: Int) async -> [LedgerEntry] { [] }
}

/// Fail-closed `IPaymentProcessor`: always declines with the empty GUID.
public final class NullPaymentProcessor: IPaymentProcessor, @unchecked Sendable {
    public static let instance = NullPaymentProcessor()
    public init() {}
    public var backendId: String { "null" }
    public func process(_ req: PaymentRequest) async -> PaymentResult {
        PaymentResult(txId: "00000000-0000-0000-0000-000000000000", accepted: false,
                      failureReason: "NullPaymentProcessor.")
    }
}
