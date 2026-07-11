// Crm.swift
//
// Port of the CRM vertical from src/CircleAI.CRM/:
//   • Contracts.cs            — Contact, Company, Deal, Activity records;
//                               IContactStore, IDealPipeline, IActivityLog
//   • InMemoryCrm.cs          — deterministic in-memory backends (name/email
//                               substring search, deals indexed by stage,
//                               activity log per contact)
//   • NullImplementations.cs  — fail-closed Null* backends
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`.
//   • `ValueTask<T>`-returning contract methods become `async` methods returning
//     the value directly; the in-memory impls complete synchronously.
//   • C# throws `ArgumentException` for blank ids / null query / non-positive
//     topK. These become `CrmError` cases thrown from the async methods.
//   • Search is case-insensitive substring on FullName OR Email, ordered
//     ascending by FullName (case-insensitive), then `Take(topK)`.
//   • Deals list-by-stage filters case-insensitively on Stage, ordered
//     descending by Value.
//   • Activity log reads newest-first (descending AtUtc), `Take(limit)`.
//   • All state guarded by a single `NSLock` per backend.

import Foundation

// MARK: - Records

/// A CRM contact.
public struct Contact: Sendable, Equatable, Codable {
    public let contactId: String
    public let fullName: String
    public let email: String?
    public let phone: String?
    public let companyId: String?

    public init(contactId: String, fullName: String, email: String?, phone: String?, companyId: String?) {
        self.contactId = contactId
        self.fullName = fullName
        self.email = email
        self.phone = phone
        self.companyId = companyId
    }
}

/// A company record.
public struct Company: Sendable, Equatable, Codable {
    public let companyId: String
    public let name: String
    public let industry: String?

    public init(companyId: String, name: String, industry: String?) {
        self.companyId = companyId
        self.name = name
        self.industry = industry
    }
}

/// A sales deal.
public struct Deal: Sendable, Equatable, Codable {
    public let dealId: String
    public let companyId: String
    public let name: String
    public let value: Decimal
    public let currency: String
    public let stage: String

    public init(dealId: String, companyId: String, name: String, value: Decimal, currency: String, stage: String) {
        self.dealId = dealId
        self.companyId = companyId
        self.name = name
        self.value = value
        self.currency = currency
        self.stage = stage
    }
}

/// A logged activity against a contact.
public struct Activity: Sendable, Equatable, Codable {
    public let activityId: String
    public let contactId: String
    public let kind: String
    public let body: String
    public let atUtc: Date

    public init(activityId: String, contactId: String, kind: String, body: String, atUtc: Date) {
        self.activityId = activityId
        self.contactId = contactId
        self.kind = kind
        self.body = body
        self.atUtc = atUtc
    }
}

// MARK: - Errors

/// Errors thrown by CRM backends. Mirrors the C# `ArgumentException` /
/// `ArgumentOutOfRangeException` guards.
public enum CrmError: Error, Equatable, CustomStringConvertible {
    case contactIdRequired
    case dealIdRequired
    case idRequired
    case queryRequired
    case stageRequired
    case contactIdArgRequired
    case topKOutOfRange

    public var description: String {
        switch self {
        case .contactIdRequired: return "ContactId required"
        case .dealIdRequired: return "DealId required"
        case .idRequired: return "id required"
        case .queryRequired: return "query required"
        case .stageRequired: return "stage required"
        case .contactIdArgRequired: return "contactId required"
        case .topKOutOfRange: return "topK out of range"
        }
    }
}

// MARK: - Contracts

/// Stores and searches contacts.
public protocol IContactStore: Sendable {
    var backendId: String { get }
    func upsert(_ c: Contact) async throws
    func get(_ id: String) async throws -> Contact?
    func search(_ query: String, topK: Int) async throws -> [Contact]
}

public extension IContactStore {
    /// Overload matching the C# default `topK = 20`.
    func search(_ query: String) async throws -> [Contact] {
        try await search(query, topK: 20)
    }
}

/// Stores and lists deals by pipeline stage.
public protocol IDealPipeline: Sendable {
    var backendId: String { get }
    func upsert(_ d: Deal) async throws
    func get(_ id: String) async -> Deal?
    func listByStage(_ stage: String) async throws -> [Deal]
}

/// Appends and reads contact activity.
public protocol IActivityLog: Sendable {
    var backendId: String { get }
    func append(_ a: Activity) async throws
    func readForContact(_ contactId: String, limit: Int) async throws -> [Activity]
}

public extension IActivityLog {
    /// Overload matching the C# default `limit = 100`.
    func readForContact(_ contactId: String) async throws -> [Activity] {
        try await readForContact(contactId, limit: 100)
    }
}

// MARK: - In-memory backends

/// Deterministic in-memory `IContactStore`. Case-insensitive substring search
/// over FullName / Email, ordered ascending by FullName.
public final class InMemoryContactStore: IContactStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: Contact] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func upsert(_ c: Contact) async throws {
        if c.contactId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CrmError.contactIdRequired }
        lock.lock(); defer { lock.unlock() }
        items[c.contactId] = c
    }

    public func get(_ id: String) async throws -> Contact? {
        if id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CrmError.idRequired }
        lock.lock(); defer { lock.unlock() }
        return items[id]
    }

    public func search(_ query: String, topK: Int) async throws -> [Contact] {
        if topK <= 0 { throw CrmError.topKOutOfRange }
        lock.lock(); defer { lock.unlock() }
        let hits = items.values.filter { c in
            c.fullName.range(of: query, options: .caseInsensitive) != nil
                || (c.email?.range(of: query, options: .caseInsensitive) != nil)
        }
        .sorted { $0.fullName.lowercased() < $1.fullName.lowercased() }
        return Array(hits.prefix(topK))
    }
}

/// Deterministic in-memory `IDealPipeline`. Deals filtered case-insensitively by
/// stage, ordered descending by value.
public final class InMemoryDealPipeline: IDealPipeline, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: Deal] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func upsert(_ d: Deal) async throws {
        if d.dealId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CrmError.dealIdRequired }
        lock.lock(); defer { lock.unlock() }
        items[d.dealId] = d
    }

    public func get(_ id: String) async -> Deal? {
        lock.lock(); defer { lock.unlock() }
        return items[id]
    }

    public func listByStage(_ stage: String) async throws -> [Deal] {
        if stage.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CrmError.stageRequired }
        lock.lock(); defer { lock.unlock() }
        return items.values
            .filter { $0.stage.caseInsensitiveCompare(stage) == .orderedSame }
            .sorted { $0.value > $1.value }
    }
}

/// Deterministic in-memory `IActivityLog`. Activities stored per contact and
/// read newest-first.
public final class InMemoryActivityLog: IActivityLog, @unchecked Sendable {
    private let lock = NSLock()
    private var byContact: [String: [Activity]] = [:]

    public init() {}
    public var backendId: String { "in-memory" }

    public func append(_ a: Activity) async throws {
        if a.contactId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CrmError.contactIdRequired }
        lock.lock(); defer { lock.unlock() }
        byContact[a.contactId, default: []].append(a)
    }

    public func readForContact(_ contactId: String, limit: Int) async throws -> [Activity] {
        if contactId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CrmError.contactIdArgRequired }
        lock.lock(); defer { lock.unlock() }
        guard let list = byContact[contactId] else { return [] }
        return Array(list.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }
}

// MARK: - Null (fail-closed) backends

/// Fail-closed `IContactStore`: stores nothing, finds nothing.
public final class NullContactStore: IContactStore, @unchecked Sendable {
    public static let instance = NullContactStore()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ c: Contact) async throws {}
    public func get(_ id: String) async throws -> Contact? { nil }
    public func search(_ query: String, topK: Int) async throws -> [Contact] { [] }
}

/// Fail-closed `IDealPipeline`.
public final class NullDealPipeline: IDealPipeline, @unchecked Sendable {
    public static let instance = NullDealPipeline()
    public init() {}
    public var backendId: String { "null" }
    public func upsert(_ d: Deal) async throws {}
    public func get(_ id: String) async -> Deal? { nil }
    public func listByStage(_ stage: String) async throws -> [Deal] { [] }
}

/// Fail-closed `IActivityLog`.
public final class NullActivityLog: IActivityLog, @unchecked Sendable {
    public static let instance = NullActivityLog()
    public init() {}
    public var backendId: String { "null" }
    public func append(_ a: Activity) async throws {}
    public func readForContact(_ contactId: String, limit: Int) async throws -> [Activity] { [] }
}
