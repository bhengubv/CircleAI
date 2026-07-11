// Family.swift
//
// Port of the Family vertical from src/CircleAI.Family/FamilyPrimitives.cs and
// the static domain-context constants from FamilyDomainContext.cs:
//   • FamilyMember, FamilyEvent, SharedExpense — domain records
//   • IFamilyBoard            — members, shared calendar, shared expenses
//   • InMemoryFamilyBoard     — deterministic in-memory impl
//   • FamilyDomainContext     — system-prompt snippet + flags
//
// The Companion-facing wrapper (FamilyCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTime`/`DateTimeOffset` → `Date`.
//   • `Members` is ordered ascending by Name.
//   • `EventsForMember` returns events whose MemberIds contain the member,
//     ordered ascending by AtUtc.
//   • `TotalPaidBy` sums expenses paid by the member at/after `since`.
//   • `SpendByCategory` sums expenses in `category` (case-insensitive) at/after
//     `since`.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A family member.
public struct FamilyMember: Sendable, Equatable, Codable {
    public let memberId: String
    public let name: String
    public let role: String
    public let dateOfBirth: Date

    public init(memberId: String, name: String, role: String, dateOfBirth: Date) {
        self.memberId = memberId
        self.name = name
        self.role = role
        self.dateOfBirth = dateOfBirth
    }
}

/// A shared family event.
public struct FamilyEvent: Sendable, Equatable, Codable {
    public let eventId: String
    public let title: String
    public let atUtc: Date
    public let memberIds: [String]

    public init(eventId: String, title: String, atUtc: Date, memberIds: [String]) {
        self.eventId = eventId
        self.title = title
        self.atUtc = atUtc
        self.memberIds = memberIds
    }
}

/// A shared expense paid by a member.
public struct SharedExpense: Sendable, Equatable, Codable {
    public let expenseId: String
    public let paidById: String
    public let amount: Decimal
    public let currency: String
    public let category: String
    public let atUtc: Date

    public init(expenseId: String, paidById: String, amount: Decimal, currency: String, category: String, atUtc: Date) {
        self.expenseId = expenseId
        self.paidById = paidById
        self.amount = amount
        self.currency = currency
        self.category = category
        self.atUtc = atUtc
    }
}

// MARK: - Contract

/// Members, shared calendar, and shared expenses for the family vertical.
public protocol IFamilyBoard: AnyObject, Sendable {
    func add(_ m: FamilyMember)
    func getMember(_ id: String) -> FamilyMember?
    var members: [FamilyMember] { get }
    func schedule(_ e: FamilyEvent)
    func eventsForMember(_ memberId: String) -> [FamilyEvent]
    func record(_ e: SharedExpense)
    func totalPaidBy(_ memberId: String, since: Date) -> Decimal
    func spendByCategory(_ category: String, since: Date) -> Decimal
}

// MARK: - InMemoryFamilyBoard

/// Deterministic in-memory `IFamilyBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryFamilyBoard: IFamilyBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var membersMap: [String: FamilyMember] = [:]
    private var events: [String: FamilyEvent] = [:]
    private var expenses: [SharedExpense] = []

    public init() {}

    public func add(_ m: FamilyMember) {
        lock.lock(); defer { lock.unlock() }
        membersMap[m.memberId] = m
    }

    public func getMember(_ id: String) -> FamilyMember? {
        lock.lock(); defer { lock.unlock() }
        return membersMap[id]
    }

    public var members: [FamilyMember] {
        lock.lock(); defer { lock.unlock() }
        return membersMap.values.sorted { $0.name < $1.name }
    }

    public func schedule(_ e: FamilyEvent) {
        lock.lock(); defer { lock.unlock() }
        events[e.eventId] = e
    }

    public func eventsForMember(_ memberId: String) -> [FamilyEvent] {
        lock.lock(); defer { lock.unlock() }
        return events.values.filter { $0.memberIds.contains(memberId) }.sorted { $0.atUtc < $1.atUtc }
    }

    public func record(_ e: SharedExpense) {
        lock.lock(); defer { lock.unlock() }
        expenses.append(e)
    }

    public func totalPaidBy(_ memberId: String, since: Date) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return expenses.filter { $0.paidById == memberId && $0.atUtc >= since }
            .reduce(Decimal.zero) { $0 + $1.amount }
    }

    public func spendByCategory(_ category: String, since: Date) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return expenses.filter { $0.category.caseInsensitiveCompare(category) == .orderedSame && $0.atUtc >= since }
            .reduce(Decimal.zero) { $0 + $1.amount }
    }
}

// MARK: - FamilyDomainContext

/// Static domain-context constants for the family vertical.
public enum FamilyDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Family] Warm family life assistant. Help with shared calendar management, family budget tracking, activity planning, milestone documentation, and family communication strategies. Respect privacy boundaries — each family member's data is their own. Compliance: POPIA, Children's Act."
    public static let complianceFlags: [String] = ["POPIA", "Childrens_Act_38_2005"]
    public static let suggestedTools: [String] = ["shared_calendar", "family_budget", "document_editor", "task_manager"]
}
