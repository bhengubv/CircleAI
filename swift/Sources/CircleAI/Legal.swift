// Legal.swift
//
// Port of the Legal vertical from src/CircleAI.Legal/LegalPrimitives.cs and the
// static domain-context constants from LegalDomainContext.cs:
//   • Matter, Contract, LegalDeadline, Clause — domain records
//   • ILegalBoard                             — matters / contracts / deadlines /
//                                               clause library contract
//   • InMemoryLegalBoard                      — deterministic in-memory impl
//   • LegalDomainContext                      — system-prompt snippet + flags
//
// The Companion-facing wrapper (LegalCompanionAdapter) is intentionally NOT
// ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `DateTime` / `DateTimeOffset` → `Date`; `DateTime?` → `Date?`
//     (Contract.expiryDate, which is nullable).
//   • `Close` on an unknown matter throws (`InvalidOperationException`) → mapped
//     onto `LegalError.unknownMatter`.
//   • `ClausesByTag` requires a non-blank tag; a blank tag throws in C#
//     (`ArgumentException`) → `LegalError.tagRequired`.
//   • Ordering: `ActiveMatters` descending by openedAtUtc;
//     `ContractsExpiringBefore` ascending by expiryDate;
//     `UpcomingDeadlines` ascending by dueOn. `ClausesByTag` preserves the C#
//     (unordered dictionary-values) semantics — insertion order is not
//     guaranteed, so tests assert on set membership.
//   • Tag matching is case-insensitive (`StringComparison.OrdinalIgnoreCase`).

import Foundation

// MARK: - Records

/// A legal matter (case / engagement).
public struct Matter: Sendable, Equatable, Codable {
    /// Stable identifier for the matter.
    public let matterId: String
    /// Matter title.
    public let title: String
    /// Governing jurisdiction.
    public let jurisdiction: String
    /// Client name.
    public let client: String
    /// When the matter was opened (UTC).
    public let openedAtUtc: Date
    /// Whether the matter is still open.
    public let open: Bool

    public init(matterId: String, title: String, jurisdiction: String, client: String,
                openedAtUtc: Date, open: Bool) {
        self.matterId = matterId
        self.title = title
        self.jurisdiction = jurisdiction
        self.client = client
        self.openedAtUtc = openedAtUtc
        self.open = open
    }
}

/// A contract attached to a matter.
public struct Contract: Sendable, Equatable, Codable {
    /// Stable identifier for the contract.
    public let contractId: String
    /// Identifier of the owning matter.
    public let matterId: String
    /// Contract title.
    public let title: String
    /// Effective date.
    public let effectiveDate: Date
    /// Expiry date, or `nil` if the contract does not expire.
    public let expiryDate: Date?
    /// Counterparty names.
    public let counterparties: [String]

    public init(contractId: String, matterId: String, title: String, effectiveDate: Date,
                expiryDate: Date?, counterparties: [String]) {
        self.contractId = contractId
        self.matterId = matterId
        self.title = title
        self.effectiveDate = effectiveDate
        self.expiryDate = expiryDate
        self.counterparties = counterparties
    }
}

/// A deadline associated with a matter.
public struct LegalDeadline: Sendable, Equatable, Codable {
    /// Stable identifier for the deadline.
    public let deadlineId: String
    /// Identifier of the owning matter.
    public let matterId: String
    /// Human-readable description.
    public let description: String
    /// Due date.
    public let dueOn: Date

    public init(deadlineId: String, matterId: String, description: String, dueOn: Date) {
        self.deadlineId = deadlineId
        self.matterId = matterId
        self.description = description
        self.dueOn = dueOn
    }
}

/// A reusable contract clause in the clause library.
public struct Clause: Sendable, Equatable, Codable {
    /// Stable identifier for the clause.
    public let clauseId: String
    /// Clause title.
    public let title: String
    /// Clause body text.
    public let body: String
    /// Tags used to categorise / search the clause.
    public let tags: [String]

    public init(clauseId: String, title: String, body: String, tags: [String]) {
        self.clauseId = clauseId
        self.title = title
        self.body = body
        self.tags = tags
    }
}

// MARK: - Errors

/// Errors thrown by the legal board.
public enum LegalError: Error, Equatable, CustomStringConvertible {
    /// `close` referenced a matter id that is not known.
    case unknownMatter(String)
    /// `clausesByTag` was called with a blank tag.
    case tagRequired

    public var description: String {
        switch self {
        case .unknownMatter(let id): return "Unknown matter \(id)"
        case .tagRequired: return "tag required"
        }
    }
}

// MARK: - ILegalBoard

/// Matters, contracts, deadlines, and a clause library for the legal vertical.
/// A synchronous contract — implementations are expected to be thread-safe.
public protocol ILegalBoard: AnyObject, Sendable {
    /// Opens (or replaces, by `matterId`) a matter.
    func open(_ m: Matter)
    /// Closes an existing matter. Throws `LegalError.unknownMatter` when unknown.
    func close(matterId: String) throws
    /// Returns the matter with `id`, or `nil`.
    func getMatter(_ id: String) -> Matter?
    /// Open matters, most-recently-opened first.
    var activeMatters: [Matter] { get }
    /// Adds (or replaces, by `contractId`) a contract.
    func addContract(_ c: Contract)
    /// Contracts whose expiry date is on or before `date`, ascending by expiry.
    func contractsExpiringBefore(_ date: Date) -> [Contract]
    /// Adds (or replaces, by `deadlineId`) a deadline.
    func add(_ d: LegalDeadline)
    /// Deadlines due on or after `now`, ascending by due date.
    func upcomingDeadlines(_ now: Date) -> [LegalDeadline]
    /// Adds (or replaces, by `clauseId`) a clause.
    func addClause(_ c: Clause)
    /// Clauses tagged `tag` (case-insensitive). Throws when `tag` is blank.
    func clausesByTag(_ tag: String) throws -> [Clause]
}

// MARK: - InMemoryLegalBoard

/// Deterministic in-memory `ILegalBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryLegalBoard: ILegalBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var matters: [String: Matter] = [:]
    private var contracts: [String: Contract] = [:]
    private var deadlines: [String: LegalDeadline] = [:]
    private var clauses: [String: Clause] = [:]

    public init() {}

    public func open(_ m: Matter) {
        lock.lock(); defer { lock.unlock() }
        matters[m.matterId] = m
    }

    public func close(matterId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let m = matters[matterId] else { throw LegalError.unknownMatter(matterId) }
        matters[matterId] = Matter(matterId: m.matterId, title: m.title, jurisdiction: m.jurisdiction,
                                   client: m.client, openedAtUtc: m.openedAtUtc, open: false)
    }

    public func getMatter(_ id: String) -> Matter? {
        lock.lock(); defer { lock.unlock() }
        return matters[id]
    }

    public var activeMatters: [Matter] {
        lock.lock(); defer { lock.unlock() }
        return matters.values.filter { $0.open }.sorted { $0.openedAtUtc > $1.openedAtUtc }
    }

    public func addContract(_ c: Contract) {
        lock.lock(); defer { lock.unlock() }
        contracts[c.contractId] = c
    }

    public func contractsExpiringBefore(_ date: Date) -> [Contract] {
        lock.lock(); defer { lock.unlock() }
        return contracts.values
            .filter { if let e = $0.expiryDate { return e <= date } else { return false } }
            .sorted { ($0.expiryDate ?? .distantFuture) < ($1.expiryDate ?? .distantFuture) }
    }

    public func add(_ d: LegalDeadline) {
        lock.lock(); defer { lock.unlock() }
        deadlines[d.deadlineId] = d
    }

    public func upcomingDeadlines(_ now: Date) -> [LegalDeadline] {
        lock.lock(); defer { lock.unlock() }
        return deadlines.values.filter { $0.dueOn >= now }.sorted { $0.dueOn < $1.dueOn }
    }

    public func addClause(_ c: Clause) {
        lock.lock(); defer { lock.unlock() }
        clauses[c.clauseId] = c
    }

    public func clausesByTag(_ tag: String) throws -> [Clause] {
        if tag.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw LegalError.tagRequired }
        lock.lock(); defer { lock.unlock() }
        return clauses.values.filter { clause in
            clause.tags.contains { $0.caseInsensitiveCompare(tag) == .orderedSame }
        }
    }
}

// MARK: - LegalDomainContext

/// Static domain-context constants for the legal vertical. Mirrors
/// `LegalDomainContext` in LegalDomainContext.cs.
public enum LegalDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Legal] You are a legal knowledge and compliance assistant. Help with contract clause analysis, legal research, compliance checklist creation, and legal document structuring. IMPORTANT: This is not legal advice. Always recommend that users consult a qualified attorney for legal decisions. Compliance: Legal Practice Act, LPA 28/2014, Attorneys Act, POPIA."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["Legal_Practice_Act_28_2014", "Attorneys_Act", "POPIA", "Professional_Legal_Privilege"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["legal_research", "document_editor", "contract_analyser"]
}
