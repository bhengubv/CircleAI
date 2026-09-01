// BusinessOpsNulls.swift
//
// Fail-closed seams: persist nothing, read empty.
//
// THE HOUSE PATTERN, AND THE ONE PLACE IT BENDS. A caller can wire the whole
// surface up before a real store exists with no risk of silently trusting
// phantom data — every read is empty and every write is a no-op. But a mutation
// that is OBLIGED to return a domain object cannot honour that: fabricating an
// invoice and handing it back means a caller shows somebody an invoice number
// that will never exist, and then reconciles against it. Those throw, by name,
// pointing at the real implementation.
//
// Ported from src/CircleAI.BusinessOps/NullImplementations.cs.

import Foundation

public enum BusinessOpsNullError: Error, Equatable, CustomStringConvertible {
    case cannotCreateReminder
    case cannotMutateInvoice

    public var description: String {
        switch self {
        case .cannotCreateReminder:
            return "NullReminderScheduler cannot create reminders. "
                 + "Use ReminderScheduler over an IBusinessStore."
        case .cannotMutateInvoice:
            return "NullInvoiceService cannot mutate invoices. "
                 + "Use InvoiceService over an IBusinessStore."
        }
    }
}

// MARK: - Store

struct NullClientRepository: IClientRepository {
    func upsert(_ client: Client) async throws {}
    func get(_ clientId: String) async throws -> Client? { nil }
    func list() async throws -> [Client] { [] }
    func remove(_ clientId: String) async throws -> Bool { false }
}

struct NullInvoiceRepository: IInvoiceRepository {
    func upsert(_ invoice: BusinessInvoice) async throws {}
    func get(_ invoiceId: String) async throws -> BusinessInvoice? { nil }
    func list() async throws -> [BusinessInvoice] { [] }
    func remove(_ invoiceId: String) async throws -> Bool { false }
}

struct NullReminderRepository: IReminderRepository {
    func upsert(_ reminder: Reminder) async throws {}
    func get(_ reminderId: String) async throws -> Reminder? { nil }
    func list() async throws -> [Reminder] { [] }
    func remove(_ reminderId: String) async throws -> Bool { false }
}

public struct NullBusinessStore: IBusinessStore, Sendable {
    public static let instance = NullBusinessStore()
    public init() {}

    public var backendId: String { "null" }
    public var clients: any IClientRepository { NullClientRepository() }
    public var invoices: any IInvoiceRepository { NullInvoiceRepository() }
    public var reminders: any IReminderRepository { NullReminderRepository() }
}

// MARK: - Client book

public struct NullClientBook: IClientBook, Sendable {
    public static let instance = NullClientBook()
    public init() {}

    public var backendId: String { "null" }

    /// Hands the client straight back UNSTORED. Safe because the caller already
    /// has it — nothing is invented — and the next `get` honestly reports nil.
    public func upsert(_ client: Client) async throws -> Client { client }

    public func get(_ clientId: String) async throws -> Client? { nil }
    public func search(_ query: String, topK: Int) async throws -> [Client] { [] }
    public func list() async throws -> [Client] { [] }
    public func remove(_ clientId: String) async throws -> Bool { false }
}

// MARK: - Reminders

public struct NullReminderScheduler: IReminderScheduler, Sendable {
    public static let instance = NullReminderScheduler()
    public init() {}

    public var backendId: String { "null" }

    public func schedule(_ reminder: Reminder) async throws -> Reminder { reminder }

    /// THROWS rather than fabricating. This one has to MINT a reminder — an id,
    /// a due date, a recurrence — and a caller handed a made-up reminder will
    /// show somebody a commitment that nothing will ever fire.
    public func scheduleFollowUp(relatedEntityId: String, title: String,
                                 dueAtUtc: Date,
                                 repeatRule: RecurrenceRule?) async throws -> Reminder {
        throw BusinessOpsNullError.cannotCreateReminder
    }

    public func get(_ reminderId: String) async throws -> Reminder? { nil }
    public func complete(_ reminderId: String) async throws -> Reminder? { nil }
    public func cancel(_ reminderId: String) async throws -> Bool { false }
    public func listDue(asOf: Date) async throws -> [Reminder] { [] }
    public func listPending() async throws -> [Reminder] { [] }
    public func listForEntity(_ relatedEntityId: String) async throws -> [Reminder] { [] }
}

// MARK: - Invoices

public struct NullInvoiceService: IInvoiceService, Sendable {
    public static let instance = NullInvoiceService()
    public init() {}

    public var backendId: String { "null" }

    // EVERY mutation throws. An invoice is money: a fabricated one means
    // somebody is shown an invoice number that will never exist, and then
    // reconciles a bank statement against it.
    public func createDraft(clientId: String, currency: String,
                            lines: [BusinessInvoiceLine], issueDate: CalendarDate,
                            paymentTermsDays: Int?, notes: String?) async throws -> BusinessInvoice {
        throw BusinessOpsNullError.cannotMutateInvoice
    }

    public func issue(_ invoiceId: String, issueDate: CalendarDate?,
                      paymentTermsDays: Int) async throws -> BusinessInvoice {
        throw BusinessOpsNullError.cannotMutateInvoice
    }

    public func recordPayment(_ invoiceId: String, amount: Money) async throws -> BusinessInvoice {
        throw BusinessOpsNullError.cannotMutateInvoice
    }

    public func markPaid(_ invoiceId: String) async throws -> BusinessInvoice {
        throw BusinessOpsNullError.cannotMutateInvoice
    }

    public func cancel(_ invoiceId: String) async throws -> BusinessInvoice {
        throw BusinessOpsNullError.cannotMutateInvoice
    }

    // Reads are honest and empty.
    public func get(_ invoiceId: String) async throws -> BusinessInvoice? { nil }
    public func list(status: InvoiceStatus?) async throws -> [BusinessInvoice] { [] }
    public func listByClient(_ clientId: String) async throws -> [BusinessInvoice] { [] }
    public func listOverdue(asOf: CalendarDate) async throws -> [BusinessInvoice] { [] }

    /// Zero, not a throw: "how many did you move to overdue" has an honest
    /// answer here, and it is none.
    public func refreshOverdue(asOf: CalendarDate) async throws -> Int { 0 }
}
