// BusinessOps.swift
//
// Running a small business from the phone: clients, invoices, reminders.
//
// Ported from src/CircleAI.BusinessOps.
//
// NAMING: this module has its own Invoice, distinct from the accounting one in
// CommerceFinance.swift. Swift has no namespaces, so these are BusinessInvoice
// and BusinessInvoiceLine. Same reason InvoiceDocument was renamed in Documents.

import Foundation

// MARK: - A date with no time on it
//
// C# has DateOnly and Foundation does not. Invoice dates MUST NOT carry a time
// or a zone: "due on the 30th" is the same day in Cape Town and in Lagos, and a
// Date would make a due-date comparison depend on where the phone is standing.

/// A calendar day. No time, no zone.
public struct CalendarDate: Sendable, Equatable, Hashable, Comparable, Codable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int

    public init(_ year: Int, _ month: Int, _ day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    /// The day this instant falls on in UTC.
    public static func from(_ date: Date) -> CalendarDate {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(secondsFromGMT: 0)!
        let c = cal.dateComponents([.year, .month, .day], from: date)
        return CalendarDate(c.year ?? 1, c.month ?? 1, c.day ?? 1)
    }

    /// The zero value, matching C# `default(DateOnly)` - used as "not set yet".
    public static let unset = CalendarDate(1, 1, 1)

    public func addingDays(_ days: Int) -> CalendarDate {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(secondsFromGMT: 0)!
        var c = DateComponents()
        c.year = year; c.month = month; c.day = day
        guard let base = cal.date(from: c),
              let moved = cal.date(byAdding: .day, value: days, to: base) else { return self }
        return CalendarDate.from(moved)
    }

    public static func < (a: CalendarDate, b: CalendarDate) -> Bool {
        (a.year, a.month, a.day) < (b.year, b.month, b.day)
    }

    /// ISO-8601 date, which is what every invoice in the world prints.
    public var description: String {
        String(format: "%04d-%02d-%02d", year, month, day)
    }
}

// MARK: - Money

public enum MoneyError: Error, CustomStringConvertible, Equatable {
    case missingCurrency
    case mixedCurrency(String, String)
    public var description: String {
        switch self {
        case .missingCurrency:
            return "Currency (ISO-4217, e.g. \"ZAR\") is required."
        case .mixedCurrency(let a, let b):
            return "Cannot combine \(a.isEmpty ? "<none>" : a) with \(b.isEmpty ? "<none>" : b). " +
                   "Convert to one currency first."
        }
    }
}

/// An amount and the currency it is in - never one without the other.
///
/// Backed by Decimal, not Double: 0.1 + 0.2 must be 0.3 on an invoice, and a
/// binary float cannot promise that.
public struct Money: Sendable, Equatable, Hashable, CustomStringConvertible {
    public let amount: Decimal
    public let currency: String

    /// Fails rather than defaulting: an amount with a guessed currency is worse
    /// than no amount at all.
    public init?(_ amount: Decimal, _ currency: String) {
        let c = currency.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if c.isEmpty { return nil }
        self.amount = amount
        self.currency = c
    }

    /// For internal arithmetic where the currency is already known-good.
    fileprivate init(unchecked amount: Decimal, currency: String) {
        self.amount = amount
        self.currency = currency
    }

    public static func zero(_ currency: String) -> Money {
        Money(0, currency) ?? Money(unchecked: 0, currency: "")
    }

    public var isZero: Bool { amount == 0 }

    /// Adding rand to naira is a bug, not a conversion - it throws.
    public static func add(_ a: Money, _ b: Money) throws -> Money {
        guard a.currency == b.currency else { throw MoneyError.mixedCurrency(a.currency, b.currency) }
        return Money(unchecked: a.amount + b.amount, currency: a.currency)
    }

    public static func subtract(_ a: Money, _ b: Money) throws -> Money {
        guard a.currency == b.currency else { throw MoneyError.mixedCurrency(a.currency, b.currency) }
        return Money(unchecked: a.amount - b.amount, currency: a.currency)
    }

    public static func * (a: Money, factor: Decimal) -> Money {
        Money(unchecked: a.amount * factor, currency: a.currency)
    }

    public static func * (factor: Decimal, a: Money) -> Money { a * factor }

    /// Half away from zero, which is what invoices and tax authorities expect -
    /// NOT bankers rounding, which is what most languages give you by default.
    public func rounded(_ decimals: Int = 2) -> Money {
        var input = amount
        var result = Decimal()
        NSDecimalRound(&result, &input, decimals, .plain)
        return Money(unchecked: result, currency: currency)
    }

    public var description: String { Currencies.format(self) }
}

/// Currency codes and how to print them.
public enum Currencies {
    public static let defaultCurrency = "ZAR"

    private static let symbols: [String: String] = [
        "ZAR": "R",   "USD": "$",   "EUR": "\u{20AC}", "GBP": "\u{A3}",
        "NGN": "\u{20A6}", "KES": "KSh", "GHS": "\u{20B5}", "TZS": "TSh",
        "UGX": "USh", "ZMW": "ZK",  "BWP": "P",   "NAD": "N$",
        "MZN": "MT",  "EGP": "E\u{A3}", "MAD": "DH",  "INR": "\u{20B9}",
    ]

    /// The symbol, or the code itself when there is no symbol for it. Never a
    /// blank - an amount with no currency marker is unreadable.
    public static func symbol(for currency: String) -> String {
        let code = currency.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if code.isEmpty { return "" }
        return symbols[code] ?? code
    }

    /// Thousands separated by a space, two decimals, invariant. A locale-driven
    /// format here would print "R 1.234,56" on a phone set to German and turn a
    /// thousand rand into one.
    public static func format(_ money: Money) -> String {
        let f = NumberFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.numberStyle = .decimal
        f.groupingSeparator = " "
        f.decimalSeparator = "."
        f.usesGroupingSeparator = true
        f.minimumFractionDigits = 2
        f.maximumFractionDigits = 2
        let body = f.string(from: money.amount as NSDecimalNumber) ?? "0.00"
        return "\(symbol(for: money.currency)) \(body)".trimmingCharacters(in: .whitespaces)
    }
}

// MARK: - Clients

/// Somebody who owes you money, and how to reach them.
public struct Client: Sendable, Equatable, Codable {
    public let clientId: String
    public let name: String
    public let email: String?
    public let phone: String?
    public let billingAddress: String?
    public let taxNumber: String?
    public let defaultCurrency: String
    public let paymentTermsDays: Int
    public let notes: String?
    public let createdAtUtc: Date?

    public init(
        clientId: String,
        name: String,
        email: String? = nil,
        phone: String? = nil,
        billingAddress: String? = nil,
        taxNumber: String? = nil,
        defaultCurrency: String = Currencies.defaultCurrency,
        paymentTermsDays: Int = 30,
        notes: String? = nil,
        createdAtUtc: Date? = nil
    ) {
        self.clientId = clientId
        self.name = name
        self.email = email
        self.phone = phone
        self.billingAddress = billingAddress
        self.taxNumber = taxNumber
        self.defaultCurrency = defaultCurrency
        self.paymentTermsDays = paymentTermsDays
        self.notes = notes
        self.createdAtUtc = createdAtUtc
    }

    public func stamped(_ at: Date) -> Client {
        createdAtUtc == nil
            ? Client(clientId: clientId, name: name, email: email, phone: phone,
                     billingAddress: billingAddress, taxNumber: taxNumber,
                     defaultCurrency: defaultCurrency, paymentTermsDays: paymentTermsDays,
                     notes: notes, createdAtUtc: at)
            : self
    }
}

public protocol IClientBook: Sendable {
    var backendId: String { get }
    func upsert(_ client: Client) async throws -> Client
    func get(_ clientId: String) async throws -> Client?
    func search(_ query: String, topK: Int) async throws -> [Client]
    func list() async throws -> [Client]
    @discardableResult func remove(_ clientId: String) async throws -> Bool
}

public extension IClientBook {
    func search(_ query: String) async throws -> [Client] { try await search(query, topK: 20) }
}

// MARK: - Invoices

public enum InvoiceStatus: Int, Sendable, Equatable, Codable {
    case draft = 0
    case sent
    case partiallyPaid
    case paid
    case overdue
    case cancelled
}

/// One line. Every figure rounds at the LINE, then lines are summed - summing
/// first and rounding once puts the total a cent out from what the customer
/// adds up by hand off the printed page.
public struct BusinessInvoiceLine: Sendable, Equatable {
    public let itemDescription: String
    public let quantity: Decimal
    public let unitPrice: Money
    public let taxRate: Decimal

    public init(description: String, quantity: Decimal, unitPrice: Money, taxRate: Decimal = 0) {
        self.itemDescription = description
        self.quantity = quantity
        self.unitPrice = unitPrice
        self.taxRate = taxRate
    }

    public var lineSubtotal: Money { (unitPrice * quantity).rounded() }
    public var lineTax: Money { (unitPrice * quantity * taxRate).rounded() }
    public var lineTotal: Money {
        Money(unchecked: lineSubtotal.amount + lineTax.amount, currency: lineSubtotal.currency)
    }
}

/// An invoice, with the arithmetic derived rather than stored - a stored total
/// that disagrees with its lines is the classic accounting bug.
public struct BusinessInvoice: Sendable, Equatable {
    public let invoiceId: String
    public let number: String?
    public let clientId: String
    public let currency: String
    public let lines: [BusinessInvoiceLine]
    public let status: InvoiceStatus
    public let issueDate: CalendarDate
    public let dueDate: CalendarDate
    public let amountPaid: Money
    public let notes: String?
    public let createdAtUtc: Date
    public let updatedAtUtc: Date

    public init(
        invoiceId: String,
        number: String? = nil,
        clientId: String,
        currency: String,
        lines: [BusinessInvoiceLine] = [],
        status: InvoiceStatus = .draft,
        issueDate: CalendarDate = .unset,
        dueDate: CalendarDate = .unset,
        amountPaid: Money? = nil,
        notes: String? = nil,
        createdAtUtc: Date = Date(timeIntervalSince1970: 0),
        updatedAtUtc: Date = Date(timeIntervalSince1970: 0)
    ) {
        self.invoiceId = invoiceId
        self.number = number
        self.clientId = clientId
        self.currency = currency
        self.lines = lines
        self.status = status
        self.issueDate = issueDate
        self.dueDate = dueDate
        self.amountPaid = amountPaid ?? Money.zero(currency)
        self.notes = notes
        self.createdAtUtc = createdAtUtc
        self.updatedAtUtc = updatedAtUtc
    }

    public var subtotal: Money { fold { $0.lineSubtotal } }
    public var taxTotal: Money { fold { $0.lineTax } }
    public var total: Money { fold { $0.lineTotal } }

    public var paidToDate: Money { amountPaid.currency.isEmpty ? Money.zero(currency) : amountPaid }

    public var balanceDue: Money {
        Money(unchecked: total.amount - paidToDate.amount, currency: currency).rounded()
    }

    public var isSettled: Bool { balanceDue.amount <= 0 }

    /// A draft is not late - it was never sent. Nor is a cancelled one.
    public func isOverdue(asOf: CalendarDate) -> Bool {
        !isSettled && status != .cancelled && status != .draft && asOf > dueDate
    }

    private func fold(_ selector: (BusinessInvoiceLine) -> Money) -> Money {
        var acc = Decimal(0)
        for line in lines { acc += selector(line).amount }
        return Money(unchecked: acc, currency: currency).rounded()
    }

    public func with(
        number: String?? = nil,
        status: InvoiceStatus? = nil,
        issueDate: CalendarDate? = nil,
        dueDate: CalendarDate? = nil,
        amountPaid: Money? = nil,
        updatedAtUtc: Date? = nil
    ) -> BusinessInvoice {
        BusinessInvoice(
            invoiceId: invoiceId,
            number: number ?? self.number,
            clientId: clientId,
            currency: currency,
            lines: lines,
            status: status ?? self.status,
            issueDate: issueDate ?? self.issueDate,
            dueDate: dueDate ?? self.dueDate,
            amountPaid: amountPaid ?? self.amountPaid,
            notes: notes,
            createdAtUtc: createdAtUtc,
            updatedAtUtc: updatedAtUtc ?? self.updatedAtUtc)
    }
}

public enum BusinessOpsError: Error, CustomStringConvertible, Equatable {
    case invoiceNotFound(String)
    case reminderNotFound(String)
    case cancelledCannotBeIssued
    case cancelledCannotBePaid
    case paidCannotBeCancelled
    case lineCurrencyMismatch(line: String, lineCurrency: String, invoiceCurrency: String)
    case paymentCurrencyMismatch(payment: String, invoice: String)
    case paymentMustBePositive
    case missingField(String)
    case noPdfRenderer

    public var description: String {
        switch self {
        case .invoiceNotFound(let id): return "Invoice \(id) not found."
        case .reminderNotFound(let id): return "Reminder \(id) not found."
        case .cancelledCannotBeIssued: return "A cancelled invoice cannot be issued."
        case .cancelledCannotBePaid: return "Cannot record a payment against a cancelled invoice."
        case .paidCannotBeCancelled:
            return "A paid invoice cannot be cancelled; issue a credit note instead."
        case .lineCurrencyMismatch(let line, let lc, let ic):
            return "Line \"\(line)\" is priced in \(lc) but the invoice is \(ic)."
        case .paymentCurrencyMismatch(let p, let i):
            return "Payment currency \(p) does not match invoice currency \(i)."
        case .paymentMustBePositive: return "A payment must be a positive amount."
        case .missingField(let f): return "\(f) is required."
        case .noPdfRenderer:
            return "No invoice PDF renderer is configured. Wire a Documents-backed renderer at the host layer."
        }
    }
}

public protocol IInvoiceService: Sendable {
    var backendId: String { get }
    func createDraft(clientId: String, currency: String, lines: [BusinessInvoiceLine],
                     issueDate: CalendarDate, paymentTermsDays: Int?, notes: String?) async throws -> BusinessInvoice
    func get(_ invoiceId: String) async throws -> BusinessInvoice?
    func issue(_ invoiceId: String, issueDate: CalendarDate?, paymentTermsDays: Int) async throws -> BusinessInvoice
    func recordPayment(_ invoiceId: String, amount: Money) async throws -> BusinessInvoice
    func markPaid(_ invoiceId: String) async throws -> BusinessInvoice
    func cancel(_ invoiceId: String) async throws -> BusinessInvoice
    func list(status: InvoiceStatus?) async throws -> [BusinessInvoice]
    func listByClient(_ clientId: String) async throws -> [BusinessInvoice]
    func listOverdue(asOf: CalendarDate) async throws -> [BusinessInvoice]
    func refreshOverdue(asOf: CalendarDate) async throws -> Int
}

public extension IInvoiceService {
    func createDraft(clientId: String, currency: String, lines: [BusinessInvoiceLine],
                     issueDate: CalendarDate) async throws -> BusinessInvoice {
        try await createDraft(clientId: clientId, currency: currency, lines: lines,
                              issueDate: issueDate, paymentTermsDays: nil, notes: nil)
    }
    func issue(_ invoiceId: String) async throws -> BusinessInvoice {
        try await issue(invoiceId, issueDate: nil, paymentTermsDays: 30)
    }
    func list() async throws -> [BusinessInvoice] { try await list(status: nil) }
}

public protocol IInvoiceNumberGenerator: Sendable {
    func next() -> String
}

/// INV-2026-0001, and the one after it is 0002. Numbering has to be sequential
/// and gapless per year - an auditor asks about gaps.
public final class SequentialInvoiceNumberGenerator: IInvoiceNumberGenerator, @unchecked Sendable {
    private let prefix: String
    private let year: Int
    private let lock = NSLock()
    private var seq: Int64

    public init(prefix: String = "INV-", year: Int? = nil, seed: Int64 = 0) {
        self.prefix = prefix
        self.year = year ?? CalendarDate.from(Date()).year
        self.seq = seed
    }

    public func next() -> String {
        lock.lock()
        seq += 1
        let n = seq
        lock.unlock()
        return "\(prefix)\(year)-\(String(format: "%04lld", n))"
    }
}

public protocol IInvoicePdfRenderer: Sendable {
    var backendId: String { get }
    func render(_ invoice: BusinessInvoice, client: Client?) async throws -> Data
}

/// Refuses, loudly. A renderer that quietly produced a blank page would be
/// worse - somebody would send it.
public struct NullInvoicePdfRenderer: IInvoicePdfRenderer {
    public static let instance = NullInvoicePdfRenderer()
    public init() {}
    public var backendId: String { "null" }
    public func render(_ invoice: BusinessInvoice, client: Client?) async throws -> Data {
        throw BusinessOpsError.noPdfRenderer
    }
}

// MARK: - Reminders

public enum Recurrence: Int, Sendable, Equatable, Codable {
    case none = 0
    case daily
    case weekly
    case monthly
    case yearly
}

public enum ReminderKind: Int, Sendable, Equatable, Codable {
    case general = 0
    case followUp
    case invoiceDue
    case custom

    public var name: String {
        switch self {
        case .general: return "General"
        case .followUp: return "FollowUp"
        case .invoiceDue: return "InvoiceDue"
        case .custom: return "Custom"
        }
    }
}

/// How often it comes back. Monthly steps by calendar month, not by 30 days -
/// a monthly check-in on the 31st must not drift to the 30th, the 29th, ...
public struct RecurrenceRule: Sendable, Equatable, Codable {
    public let kind: Recurrence
    public let interval: Int

    public init(_ kind: Recurrence, interval: Int = 1) {
        self.kind = kind
        self.interval = interval
    }

    public static let once = RecurrenceRule(.none, interval: 0)

    public var isRecurring: Bool { kind != .none }

    public func next(from: Date) -> Date? {
        let step = interval <= 0 ? 1 : interval
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(secondsFromGMT: 0)!
        switch kind {
        case .daily: return cal.date(byAdding: .day, value: step, to: from)
        case .weekly: return cal.date(byAdding: .day, value: 7 * step, to: from)
        case .monthly: return cal.date(byAdding: .month, value: step, to: from)
        case .yearly: return cal.date(byAdding: .year, value: step, to: from)
        case .none: return nil
        }
    }
}

public struct Reminder: Sendable, Equatable {
    public let reminderId: String
    public let title: String
    public let dueAtUtc: Date
    public let repeatRule: RecurrenceRule
    public let kind: ReminderKind
    public let relatedEntityId: String?
    public let completed: Bool
    public let notes: String?
    public let createdAtUtc: Date?

    public init(
        reminderId: String,
        title: String,
        dueAtUtc: Date,
        repeatRule: RecurrenceRule = .once,
        kind: ReminderKind = .general,
        relatedEntityId: String? = nil,
        completed: Bool = false,
        notes: String? = nil,
        createdAtUtc: Date? = nil
    ) {
        self.reminderId = reminderId
        self.title = title
        self.dueAtUtc = dueAtUtc
        self.repeatRule = repeatRule
        self.kind = kind
        self.relatedEntityId = relatedEntityId
        self.completed = completed
        self.notes = notes
        self.createdAtUtc = createdAtUtc
    }

    public func isDue(asOf: Date) -> Bool { !completed && asOf >= dueAtUtc }

    public func with(reminderId: String? = nil, dueAtUtc: Date? = nil,
                     completed: Bool? = nil, createdAtUtc: Date?? = nil) -> Reminder {
        Reminder(
            reminderId: reminderId ?? self.reminderId,
            title: title,
            dueAtUtc: dueAtUtc ?? self.dueAtUtc,
            repeatRule: repeatRule,
            kind: kind,
            relatedEntityId: relatedEntityId,
            completed: completed ?? self.completed,
            notes: notes,
            createdAtUtc: createdAtUtc ?? self.createdAtUtc)
    }
}

public protocol IReminderScheduler: Sendable {
    var backendId: String { get }
    func schedule(_ reminder: Reminder) async throws -> Reminder
    func scheduleFollowUp(relatedEntityId: String, title: String, dueAtUtc: Date,
                          repeatRule: RecurrenceRule?) async throws -> Reminder
    func get(_ reminderId: String) async throws -> Reminder?
    /// Returns the NEXT occurrence for a recurring reminder, or nil for a one-off.
    func complete(_ reminderId: String) async throws -> Reminder?
    @discardableResult func cancel(_ reminderId: String) async throws -> Bool
    func listDue(asOf: Date) async throws -> [Reminder]
    func listPending() async throws -> [Reminder]
    func listForEntity(_ relatedEntityId: String) async throws -> [Reminder]
}

// MARK: - Storage

public protocol IClientRepository: Sendable {
    func upsert(_ client: Client) async throws
    func get(_ clientId: String) async throws -> Client?
    func list() async throws -> [Client]
    func remove(_ clientId: String) async throws -> Bool
}

public protocol IInvoiceRepository: Sendable {
    func upsert(_ invoice: BusinessInvoice) async throws
    func get(_ invoiceId: String) async throws -> BusinessInvoice?
    func list() async throws -> [BusinessInvoice]
    func remove(_ invoiceId: String) async throws -> Bool
}

public protocol IReminderRepository: Sendable {
    func upsert(_ reminder: Reminder) async throws
    func get(_ reminderId: String) async throws -> Reminder?
    func list() async throws -> [Reminder]
    func remove(_ reminderId: String) async throws -> Bool
}

public protocol IBusinessStore: Sendable {
    var backendId: String { get }
    var clients: any IClientRepository { get }
    var invoices: any IInvoiceRepository { get }
    var reminders: any IReminderRepository { get }
}

/// A tiny generic store. Everything here is keyed by a string id and sorted on
/// the way out, so listing order never depends on insertion order.
final class KeyedStore<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: T] = [:]

    func put(_ key: String, _ value: T) {
        lock.lock(); items[key] = value; lock.unlock()
    }
    func fetch(_ key: String) -> T? {
        lock.lock(); defer { lock.unlock() }
        return items[key]
    }
    func all() -> [T] {
        lock.lock(); defer { lock.unlock() }
        return Array(items.values)
    }
    func drop(_ key: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return items.removeValue(forKey: key) != nil
    }
}

public final class InMemoryClientRepository: IClientRepository, @unchecked Sendable {
    private let store = KeyedStore<Client>()
    public init() {}
    public func upsert(_ client: Client) async throws {
        guard !client.clientId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("clientId")
        }
        store.put(client.clientId, client)
    }
    public func get(_ clientId: String) async throws -> Client? { store.fetch(clientId) }
    public func list() async throws -> [Client] {
        store.all().sorted { $0.name.lowercased() < $1.name.lowercased() }
    }
    public func remove(_ clientId: String) async throws -> Bool { store.drop(clientId) }
}

public final class InMemoryInvoiceRepository: IInvoiceRepository, @unchecked Sendable {
    private let store = KeyedStore<BusinessInvoice>()
    public init() {}
    public func upsert(_ invoice: BusinessInvoice) async throws {
        guard !invoice.invoiceId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("invoiceId")
        }
        store.put(invoice.invoiceId, invoice)
    }
    public func get(_ invoiceId: String) async throws -> BusinessInvoice? { store.fetch(invoiceId) }
    public func list() async throws -> [BusinessInvoice] { store.all() }
    public func remove(_ invoiceId: String) async throws -> Bool { store.drop(invoiceId) }
}

public final class InMemoryReminderRepository: IReminderRepository, @unchecked Sendable {
    private let store = KeyedStore<Reminder>()
    public init() {}
    public func upsert(_ reminder: Reminder) async throws {
        guard !reminder.reminderId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("reminderId")
        }
        store.put(reminder.reminderId, reminder)
    }
    public func get(_ reminderId: String) async throws -> Reminder? { store.fetch(reminderId) }
    public func list() async throws -> [Reminder] { store.all() }
    public func remove(_ reminderId: String) async throws -> Bool { store.drop(reminderId) }
}

public struct InMemoryBusinessStore: IBusinessStore {
    public let clients: any IClientRepository
    public let invoices: any IInvoiceRepository
    public let reminders: any IReminderRepository

    public init() {
        clients = InMemoryClientRepository()
        invoices = InMemoryInvoiceRepository()
        reminders = InMemoryReminderRepository()
    }

    public var backendId: String { "in-memory" }
}

// MARK: - Services

enum BusinessOpsIds {
    static func new() -> String { UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }
}

/// A clock, so every date in a test is decided by the test.
public protocol IBusinessClock: Sendable {
    func now() -> Date
}

public struct SystemBusinessClock: IBusinessClock {
    public init() {}
    public func now() -> Date { Date() }
}

public struct FixedBusinessClock: IBusinessClock {
    private let instant: Date
    public init(_ instant: Date) { self.instant = instant }
    public func now() -> Date { instant }
}

public struct ClientBook: IClientBook {
    private let repo: any IClientRepository
    private let clock: any IBusinessClock

    public init(store: any IBusinessStore, clock: (any IBusinessClock)? = nil) {
        self.repo = store.clients
        self.clock = clock ?? SystemBusinessClock()
    }

    public var backendId: String { "default" }

    public func upsert(_ client: Client) async throws -> Client {
        guard !client.clientId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("clientId")
        }
        let stamped = client.stamped(clock.now())
        try await repo.upsert(stamped)
        return stamped
    }

    public func get(_ clientId: String) async throws -> Client? { try await repo.get(clientId) }

    /// Name, email or phone - the three things somebody actually remembers.
    public func search(_ query: String, topK: Int = 20) async throws -> [Client] {
        guard topK > 0 else { return [] }
        let q = query.lowercased()
        return try await repo.list().filter {
            $0.name.lowercased().contains(q)
                || ($0.email?.lowercased().contains(q) ?? false)
                || ($0.phone?.lowercased().contains(q) ?? false)
        }.prefix(topK).map { $0 }
    }

    public func list() async throws -> [Client] { try await repo.list() }

    @discardableResult
    public func remove(_ clientId: String) async throws -> Bool { try await repo.remove(clientId) }
}

public struct InvoiceService: IInvoiceService {
    private let invoices: any IInvoiceRepository
    private let clients: any IClientRepository
    private let numbers: any IInvoiceNumberGenerator
    private let clock: any IBusinessClock

    public init(store: any IBusinessStore,
                numbers: (any IInvoiceNumberGenerator)? = nil,
                clock: (any IBusinessClock)? = nil) {
        self.invoices = store.invoices
        self.clients = store.clients
        self.numbers = numbers ?? SequentialInvoiceNumberGenerator()
        self.clock = clock ?? SystemBusinessClock()
    }

    public var backendId: String { "default" }

    public func createDraft(clientId: String, currency: String, lines: [BusinessInvoiceLine],
                            issueDate: CalendarDate, paymentTermsDays: Int? = nil,
                            notes: String? = nil) async throws -> BusinessInvoice {
        guard !clientId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("clientId")
        }
        let cur = currency.trimmingCharacters(in: .whitespaces).uppercased()
        guard !cur.isEmpty else { throw BusinessOpsError.missingField("currency") }

        // Refuse a mixed-currency invoice at creation. Caught later it is a
        // total that silently means nothing.
        for l in lines where l.unitPrice.currency != cur {
            throw BusinessOpsError.lineCurrencyMismatch(
                line: l.itemDescription, lineCurrency: l.unitPrice.currency, invoiceCurrency: cur)
        }

        // Explicit terms win; otherwise the terms on the client record;
        // otherwise 30. Written out rather than chained with ?? because the
        // middle step is both async and throwing, which an autoclosure cannot
        // carry.
        let terms: Int
        if let explicit = paymentTermsDays {
            terms = explicit
        } else {
            terms = (try await clients.get(clientId))?.paymentTermsDays ?? 30
        }
        let now = clock.now()
        let invoice = BusinessInvoice(
            invoiceId: BusinessOpsIds.new(),
            clientId: clientId,
            currency: cur,
            lines: lines,
            status: .draft,
            issueDate: issueDate,
            dueDate: issueDate.addingDays(terms),
            amountPaid: Money.zero(cur),
            notes: notes,
            createdAtUtc: now,
            updatedAtUtc: now)
        try await invoices.upsert(invoice)
        return invoice
    }

    public func get(_ invoiceId: String) async throws -> BusinessInvoice? {
        try await invoices.get(invoiceId)
    }

    public func issue(_ invoiceId: String, issueDate: CalendarDate? = nil,
                      paymentTermsDays: Int = 30) async throws -> BusinessInvoice {
        let inv = try await require(invoiceId)
        guard inv.status != .cancelled else { throw BusinessOpsError.cancelledCannotBeIssued }

        var issue = issueDate ?? inv.issueDate
        if issue == .unset { issue = CalendarDate.from(clock.now()) }
        let due = inv.dueDate == .unset ? issue.addingDays(paymentTermsDays) : inv.dueDate

        // The number is assigned ONCE, on first issue. Re-issuing must not
        // renumber it - the customer already has the old number.
        let updated = inv.with(
            number: .some(inv.number ?? numbers.next()),
            status: inv.status == .draft ? .sent : inv.status,
            issueDate: issue,
            dueDate: due,
            updatedAtUtc: clock.now())
        try await invoices.upsert(updated)
        return updated
    }

    private func require(_ invoiceId: String) async throws -> BusinessInvoice {
        guard !invoiceId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("invoiceId")
        }
        guard let inv = try await invoices.get(invoiceId) else {
            throw BusinessOpsError.invoiceNotFound(invoiceId)
        }
        return inv
    }
}

public extension InvoiceService {

    func recordPayment(_ invoiceId: String, amount: Money) async throws -> BusinessInvoice {
        let inv = try await require(invoiceId)
        guard inv.status != .cancelled else { throw BusinessOpsError.cancelledCannotBePaid }
        guard amount.currency == inv.currency else {
            throw BusinessOpsError.paymentCurrencyMismatch(payment: amount.currency, invoice: inv.currency)
        }
        guard amount.amount > 0 else { throw BusinessOpsError.paymentMustBePositive }

        let paid = try Money.add(inv.paidToDate, amount).rounded()
        let status: InvoiceStatus = paid.amount >= inv.total.amount ? .paid : .partiallyPaid
        let updated = inv.with(status: status, amountPaid: paid, updatedAtUtc: clock.now())
        try await invoices.upsert(updated)
        return updated
    }

    /// Settles whatever is left. An already-settled invoice is just marked paid
    /// rather than being handed a zero payment, which would be refused.
    func markPaid(_ invoiceId: String) async throws -> BusinessInvoice {
        let inv = try await require(invoiceId)
        guard inv.status != .cancelled else { throw BusinessOpsError.cancelledCannotBePaid }

        let balance = inv.balanceDue
        if balance.amount <= 0 {
            if inv.status == .paid { return inv }
            let already = inv.with(status: .paid, updatedAtUtc: clock.now())
            try await invoices.upsert(already)
            return already
        }
        return try await recordPayment(invoiceId, amount: balance)
    }

    func cancel(_ invoiceId: String) async throws -> BusinessInvoice {
        let inv = try await require(invoiceId)
        guard inv.status != .paid else { throw BusinessOpsError.paidCannotBeCancelled }
        let updated = inv.with(status: .cancelled, updatedAtUtc: clock.now())
        try await invoices.upsert(updated)
        return updated
    }

    func list(status: InvoiceStatus? = nil) async throws -> [BusinessInvoice] {
        let all = try await invoices.list()
        let filtered = status.map { s in all.filter { $0.status == s } } ?? all
        return filtered.sorted { $0.issueDate > $1.issueDate }
    }

    func listByClient(_ clientId: String) async throws -> [BusinessInvoice] {
        guard !clientId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("clientId")
        }
        return try await invoices.list()
            .filter { $0.clientId == clientId }
            .sorted { $0.issueDate > $1.issueDate }
    }

    func listOverdue(asOf: CalendarDate) async throws -> [BusinessInvoice] {
        try await invoices.list()
            .filter { $0.isOverdue(asOf: asOf) }
            .sorted { $0.dueDate < $1.dueDate }
    }

    /// Stamps the overdue status onto anything that has passed its due date.
    /// Returns how many changed, so a caller can decide whether to say anything.
    func refreshOverdue(asOf: CalendarDate) async throws -> Int {
        var changed = 0
        for inv in try await invoices.list() where inv.isOverdue(asOf: asOf) && inv.status != .overdue {
            try await invoices.upsert(inv.with(status: .overdue, updatedAtUtc: clock.now()))
            changed += 1
        }
        return changed
    }
}

public struct ReminderScheduler: IReminderScheduler {
    private let repo: any IReminderRepository
    private let clock: any IBusinessClock

    public init(store: any IBusinessStore, clock: (any IBusinessClock)? = nil) {
        self.repo = store.reminders
        self.clock = clock ?? SystemBusinessClock()
    }

    public var backendId: String { "default" }

    public func schedule(_ reminder: Reminder) async throws -> Reminder {
        guard !reminder.reminderId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("reminderId")
        }
        guard !reminder.title.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("title")
        }
        let stamped = reminder.createdAtUtc == nil
            ? reminder.with(createdAtUtc: .some(clock.now()))
            : reminder
        try await repo.upsert(stamped)
        return stamped
    }

    public func scheduleFollowUp(relatedEntityId: String, title: String, dueAtUtc: Date,
                                 repeatRule: RecurrenceRule? = nil) async throws -> Reminder {
        guard !relatedEntityId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("relatedEntityId")
        }
        guard !title.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("title")
        }
        return try await schedule(Reminder(
            reminderId: BusinessOpsIds.new(),
            title: title,
            dueAtUtc: dueAtUtc,
            repeatRule: repeatRule ?? .once,
            kind: .followUp,
            relatedEntityId: relatedEntityId,
            createdAtUtc: clock.now()))
    }

    public func get(_ reminderId: String) async throws -> Reminder? { try await repo.get(reminderId) }

    /// Completing a recurring reminder schedules the NEXT one and returns it -
    /// a repeating reminder that stops after the first tick is just a reminder.
    public func complete(_ reminderId: String) async throws -> Reminder? {
        guard !reminderId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("reminderId")
        }
        guard let existing = try await repo.get(reminderId) else {
            throw BusinessOpsError.reminderNotFound(reminderId)
        }
        try await repo.upsert(existing.with(completed: true))

        guard existing.repeatRule.isRecurring,
              let next = existing.repeatRule.next(from: existing.dueAtUtc) else { return nil }

        let followOn = existing.with(
            reminderId: BusinessOpsIds.new(),
            dueAtUtc: next,
            completed: false,
            createdAtUtc: .some(clock.now()))
        try await repo.upsert(followOn)
        return followOn
    }

    @discardableResult
    public func cancel(_ reminderId: String) async throws -> Bool { try await repo.remove(reminderId) }

    public func listDue(asOf: Date) async throws -> [Reminder] {
        try await repo.list().filter { $0.isDue(asOf: asOf) }.sorted { $0.dueAtUtc < $1.dueAtUtc }
    }

    public func listPending() async throws -> [Reminder] {
        try await repo.list().filter { !$0.completed }.sorted { $0.dueAtUtc < $1.dueAtUtc }
    }

    public func listForEntity(_ relatedEntityId: String) async throws -> [Reminder] {
        guard !relatedEntityId.trimmingCharacters(in: .whitespaces).isEmpty else {
            throw BusinessOpsError.missingField("relatedEntityId")
        }
        return try await repo.list()
            .filter { $0.relatedEntityId == relatedEntityId }
            .sorted { $0.dueAtUtc < $1.dueAtUtc }
    }
}

// MARK: - CRM bridge
//
// The same person is a Client here and a Contact in the CRM. These convert
// between them rather than duplicating the record.

public extension Client {
    func toContact(companyId: String? = nil) -> Contact {
        Contact(contactId: clientId, fullName: name, email: email, phone: phone, companyId: companyId)
    }
}

public extension Contact {
    func toClient(defaultCurrency: String = Currencies.defaultCurrency,
                  paymentTermsDays: Int = 30) -> Client {
        Client(clientId: contactId, name: fullName, email: email, phone: phone,
               defaultCurrency: defaultCurrency, paymentTermsDays: paymentTermsDays)
    }
}

public extension Reminder {
    func toActivity(contactId: String) -> Activity {
        Activity(activityId: reminderId, contactId: contactId,
                 kind: kind.name, body: title, atUtc: dueAtUtc)
    }
}

public enum CrmBridge {
    /// Copies every client into the CRM as a contact. Returns how many.
    @discardableResult
    public static func mirrorToCrm(clients: any IClientBook,
                                   contacts: any IContactStore) async throws -> Int {
        var n = 0
        for client in try await clients.list() {
            try await contacts.upsert(client.toContact())
            n += 1
        }
        return n
    }
}

// MARK: - Sample data
//
// Three real-shaped clients across two currencies and two payment terms, so a
// demo screen shows what the module actually has to handle.

public enum BusinessOpsSampleData {

    public static func clients() -> [Client] {
        [
            Client(clientId: "cl-nandi", name: "Nandi Dlamini Design",
                   email: "nandi@example.co.za", phone: "+27 82 555 0142",
                   billingAddress: "12 Long St, Cape Town, 8001",
                   taxNumber: "4470112345", defaultCurrency: "ZAR", paymentTermsDays: 30),
            Client(clientId: "cl-thabo", name: "Thabo Trading CC",
                   email: "accounts@thabo.example", phone: "+27 71 555 0199",
                   billingAddress: "5 Jan Smuts Ave, Johannesburg, 2196",
                   taxNumber: "4990556677", defaultCurrency: "ZAR", paymentTermsDays: 14),
            Client(clientId: "cl-amara", name: "Amara Studios (Lagos)",
                   email: "hello@amara.example", phone: "+234 802 555 0101",
                   billingAddress: "3 Awolowo Rd, Ikoyi, Lagos",
                   taxNumber: nil, defaultCurrency: "NGN", paymentTermsDays: 30),
        ]
    }

    public static func sampleInvoice(invoiceId: String = "inv-sample-1",
                                     clientId: String = "cl-nandi",
                                     currency: String = "ZAR") -> BusinessInvoice {
        let issue = CalendarDate(2026, 7, 1)
        let stamp = Date(timeIntervalSince1970: 1_782_896_400)  // 2026-07-01T09:00:00Z
        let lines = [
            BusinessInvoiceLine(description: "Brand identity - logo suite",
                                quantity: 1, unitPrice: Money(8500, currency)!, taxRate: 0.15),
            BusinessInvoiceLine(description: "Business cards - design",
                                quantity: 2, unitPrice: Money(750, currency)!, taxRate: 0.15),
        ]
        return BusinessInvoice(
            invoiceId: invoiceId,
            number: "INV-2026-0001",
            clientId: clientId,
            currency: currency,
            lines: lines,
            status: .sent,
            issueDate: issue,
            dueDate: issue.addingDays(30),
            amountPaid: Money.zero(currency),
            notes: "Thank you for your business. Banking details overleaf.",
            createdAtUtc: stamp,
            updatedAtUtc: stamp)
    }

    public static func reminders() -> [Reminder] {
        let created = Date(timeIntervalSince1970: 1_782_896_400)   // 2026-07-01T09:00:00Z
        return [
            Reminder(reminderId: "rem-chase-inv1",
                     title: "Follow up on INV-2026-0001",
                     dueAtUtc: Date(timeIntervalSince1970: 1_784_534_400),  // 2026-07-20T08:00:00Z
                     kind: .invoiceDue,
                     relatedEntityId: "inv-sample-1",
                     createdAtUtc: created),
            Reminder(reminderId: "rem-checkin-thabo",
                     title: "Monthly check-in call",
                     dueAtUtc: Date(timeIntervalSince1970: 1_785_571_200),  // 2026-08-01T08:00:00Z
                     repeatRule: RecurrenceRule(.monthly),
                     kind: .followUp,
                     relatedEntityId: "cl-thabo",
                     createdAtUtc: created),
        ]
    }
}
