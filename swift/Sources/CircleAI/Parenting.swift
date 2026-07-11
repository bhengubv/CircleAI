// Parenting.swift
//
// Port of the Parenting vertical from src/CircleAI.Parenting/ParentingPrimitives.cs
// and the static domain-context constants from ParentingDomainContext.cs:
//   • DayOfWeek (enum)                          — .NET-compatible (Sunday=0 …)
//   • Child, Milestone, RoutineEntry, Routine   — domain records
//   • IParentingBoard                           — children, milestones, routines
//   • InMemoryParentingBoard                    — deterministic in-memory impl
//   • ParentingDomainContext                    — system-prompt snippet + flags
//
// The Companion-facing wrapper (ParentingCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `DateTime`/`DateTimeOffset` → `Date`.
//   • `System.DayOfWeek` → `DayOfWeek` here, an `Int`-backed enum matching the
//     .NET numbering (Sunday = 0 … Saturday = 6) so routine keys and Codable
//     round-trips are identical to C#.
//   • `TimeSpan AgeAsOf(...)` → `TimeInterval` (seconds); computed as
//     `at - child.dateOfBirth`. Unknown child throws
//     `ParentingError.unknownChild`.
//   • `RecordMilestone` requires a non-blank ChildId (`.childIdRequired`).
//   • `Children` is ordered ascending by Name. `MilestonesFor` returns the
//     child's milestones newest-first (by AchievedAtUtc); unknown child → [].
//   • Routine key is `"{childId}/{dayOfWeek}"`.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Enum

/// A day of the week, numbered to match .NET `System.DayOfWeek`
/// (Sunday = 0 … Saturday = 6).
public enum DayOfWeek: Int, Sendable, Equatable, Codable, CaseIterable {
    case sunday = 0
    case monday = 1
    case tuesday = 2
    case wednesday = 3
    case thursday = 4
    case friday = 5
    case saturday = 6
}

// MARK: - Records

/// A child.
public struct Child: Sendable, Equatable, Codable {
    public let childId: String
    public let name: String
    public let dateOfBirth: Date
    public let gender: String?

    public init(childId: String, name: String, dateOfBirth: Date, gender: String?) {
        self.childId = childId
        self.name = name
        self.dateOfBirth = dateOfBirth
        self.gender = gender
    }
}

/// A developmental milestone.
public struct Milestone: Sendable, Equatable, Codable {
    public let milestoneId: String
    public let childId: String
    public let category: String
    public let description: String
    public let achievedAtUtc: Date

    public init(milestoneId: String, childId: String, category: String, description: String, achievedAtUtc: Date) {
        self.milestoneId = milestoneId
        self.childId = childId
        self.category = category
        self.description = description
        self.achievedAtUtc = achievedAtUtc
    }
}

/// A single entry in a daily routine.
public struct RoutineEntry: Sendable, Equatable, Codable {
    public let time: String
    public let activity: String

    public init(time: String, activity: String) {
        self.time = time
        self.activity = activity
    }
}

/// A child's routine for a given day of the week.
public struct Routine: Sendable, Equatable, Codable {
    public let childId: String
    public let dayOfWeek: DayOfWeek
    public let entries: [RoutineEntry]

    public init(childId: String, dayOfWeek: DayOfWeek, entries: [RoutineEntry]) {
        self.childId = childId
        self.dayOfWeek = dayOfWeek
        self.entries = entries
    }
}

// MARK: - Errors

public enum ParentingError: Error, Equatable, CustomStringConvertible {
    case childIdRequired
    case unknownChild(String)

    public var description: String {
        switch self {
        case .childIdRequired: return "ChildId required"
        case .unknownChild(let id): return "Unknown child \(id)"
        }
    }
}

// MARK: - Contract

/// Children, milestones, and daily routines for the parenting vertical.
public protocol IParentingBoard: AnyObject, Sendable {
    func addChild(_ c: Child)
    func getChild(_ id: String) -> Child?
    var children: [Child] { get }
    func recordMilestone(_ m: Milestone) throws
    func milestonesFor(_ childId: String) -> [Milestone]
    func setRoutine(_ r: Routine)
    func getRoutine(childId: String, dow: DayOfWeek) -> Routine?
    func ageAsOf(childId: String, at: Date) throws -> TimeInterval
}

// MARK: - InMemoryParentingBoard

/// Deterministic in-memory `IParentingBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryParentingBoard: IParentingBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var childrenMap: [String: Child] = [:]
    private var milestones: [String: [Milestone]] = [:]
    private var routines: [String: Routine] = [:]

    public init() {}

    public func addChild(_ c: Child) {
        lock.lock(); defer { lock.unlock() }
        childrenMap[c.childId] = c
    }

    public func getChild(_ id: String) -> Child? {
        lock.lock(); defer { lock.unlock() }
        return childrenMap[id]
    }

    public var children: [Child] {
        lock.lock(); defer { lock.unlock() }
        return childrenMap.values.sorted { $0.name < $1.name }
    }

    public func recordMilestone(_ m: Milestone) throws {
        if m.childId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw ParentingError.childIdRequired }
        lock.lock(); defer { lock.unlock() }
        milestones[m.childId, default: []].append(m)
    }

    public func milestonesFor(_ childId: String) -> [Milestone] {
        lock.lock(); defer { lock.unlock() }
        guard let list = milestones[childId] else { return [] }
        return list.sorted { $0.achievedAtUtc > $1.achievedAtUtc }
    }

    public func setRoutine(_ r: Routine) {
        lock.lock(); defer { lock.unlock() }
        routines[Self.key(r.childId, r.dayOfWeek)] = r
    }

    public func getRoutine(childId: String, dow: DayOfWeek) -> Routine? {
        lock.lock(); defer { lock.unlock() }
        return routines[Self.key(childId, dow)]
    }

    public func ageAsOf(childId: String, at: Date) throws -> TimeInterval {
        lock.lock(); defer { lock.unlock() }
        guard let c = childrenMap[childId] else { throw ParentingError.unknownChild(childId) }
        return at.timeIntervalSince(c.dateOfBirth)
    }

    private static func key(_ childId: String, _ d: DayOfWeek) -> String { "\(childId)/\(d)" }
}

// MARK: - ParentingDomainContext

/// Static domain-context constants for the parenting vertical.
public enum ParentingDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Parenting] Supportive parenting companion. Offer evidence-based parenting strategies (positive discipline, attachment, development milestones), school communication guidance, and family wellbeing tips. Acknowledge the difficulty of parenting without judgment. Compliance: Children's Act 38/2005, POPIA."
    public static let complianceFlags: [String] = ["Childrens_Act_38_2005", "POPIA"]
    public static let suggestedTools: [String] = ["development_tracker", "document_editor", "web_search", "calendar"]
}
