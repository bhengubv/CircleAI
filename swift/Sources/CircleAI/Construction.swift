// Construction.swift
//
// Port of the Construction vertical from
// src/CircleAI.Construction/ConstructionPrimitives.cs and the static
// domain-context constants from ConstructionDomainContext.cs:
//   • Project, ConstructionTask, CostEntry — domain records
//   • IConstructionBoard                   — projects, tasks, costs, budget
//   • InMemoryConstructionBoard            — deterministic in-memory impl
//   • ConstructionDomainContext            — system-prompt snippet + flags
//
// The Companion-facing wrapper (ConstructionCompanionAdapter) is an
// ICompanionSession decorator that prefixes the construction domain prompt.
//
// Porting notes:
//   • `decimal Budget/Amount` → `Decimal`; `DateTime`/`DateTimeOffset` → `Date`;
//     `DateTime? EndOn` → `Date?`.
//   • `Complete` on an unknown task throws `.unknownTask`.
//   • `OpenConstructionTasksFor` returns incomplete tasks for the project,
//     ordered ascending by DueOn.
//   • `SpendFor` sums cost amounts for the project. `RemainingBudget` on an
//     unknown project throws `.unknownProject`. All state guarded by a single
//     `NSLock` (spend total is a non-locking private helper).

import Foundation

// MARK: - Records

/// A construction project.
public struct Project: Sendable, Equatable, Codable {
    public let projectId: String
    public let name: String
    public let startOn: Date
    public let endOn: Date?
    public let budget: Decimal
    public let currency: String

    public init(projectId: String, name: String, startOn: Date, endOn: Date?, budget: Decimal, currency: String) {
        self.projectId = projectId
        self.name = name
        self.startOn = startOn
        self.endOn = endOn
        self.budget = budget
        self.currency = currency
    }
}

/// A construction task.
public struct ConstructionTask: Sendable, Equatable, Codable {
    public let constructionTaskId: String
    public let projectId: String
    public let description: String
    public let dueOn: Date
    public let completed: Bool

    public init(constructionTaskId: String, projectId: String, description: String, dueOn: Date, completed: Bool) {
        self.constructionTaskId = constructionTaskId
        self.projectId = projectId
        self.description = description
        self.dueOn = dueOn
        self.completed = completed
    }
}

/// A cost entry against a project.
public struct CostEntry: Sendable, Equatable, Codable {
    public let entryId: String
    public let projectId: String
    public let category: String
    public let amount: Decimal
    public let atUtc: Date

    public init(entryId: String, projectId: String, category: String, amount: Decimal, atUtc: Date) {
        self.entryId = entryId
        self.projectId = projectId
        self.category = category
        self.amount = amount
        self.atUtc = atUtc
    }
}

// MARK: - Errors

public enum ConstructionError: Error, Equatable, CustomStringConvertible {
    case unknownTask(String)
    case unknownProject(String)

    public var description: String {
        switch self {
        case .unknownTask(let id): return "Unknown task \(id)"
        case .unknownProject(let id): return "Unknown project \(id)"
        }
    }
}

// MARK: - Contract

/// Projects, tasks, costs, and budgets for the construction vertical.
public protocol IConstructionBoard: AnyObject, Sendable {
    func create(_ p: Project)
    func getProject(_ id: String) -> Project?
    func add(_ t: ConstructionTask)
    func complete(taskId: String) throws
    func openConstructionTasksFor(projectId: String) -> [ConstructionTask]
    func recordCost(_ c: CostEntry)
    func spendFor(projectId: String) -> Decimal
    func remainingBudget(projectId: String) throws -> Decimal
}

// MARK: - InMemoryConstructionBoard

/// Deterministic in-memory `IConstructionBoard`. All state guarded by a single `NSLock`.
public final class InMemoryConstructionBoard: IConstructionBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var projects: [String: Project] = [:]
    private var tasks: [String: ConstructionTask] = [:]
    private var costs: [CostEntry] = []

    public init() {}

    public func create(_ p: Project) {
        lock.lock(); defer { lock.unlock() }
        projects[p.projectId] = p
    }

    public func getProject(_ id: String) -> Project? {
        lock.lock(); defer { lock.unlock() }
        return projects[id]
    }

    public func add(_ t: ConstructionTask) {
        lock.lock(); defer { lock.unlock() }
        tasks[t.constructionTaskId] = t
    }

    public func complete(taskId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let t = tasks[taskId] else { throw ConstructionError.unknownTask(taskId) }
        tasks[taskId] = ConstructionTask(constructionTaskId: t.constructionTaskId, projectId: t.projectId, description: t.description, dueOn: t.dueOn, completed: true)
    }

    public func openConstructionTasksFor(projectId: String) -> [ConstructionTask] {
        lock.lock(); defer { lock.unlock() }
        return tasks.values.filter { $0.projectId == projectId && !$0.completed }.sorted { $0.dueOn < $1.dueOn }
    }

    public func recordCost(_ c: CostEntry) {
        lock.lock(); defer { lock.unlock() }
        costs.append(c)
    }

    public func spendFor(projectId: String) -> Decimal {
        lock.lock(); defer { lock.unlock() }
        return spendForLocked(projectId)
    }

    public func remainingBudget(projectId: String) throws -> Decimal {
        lock.lock(); defer { lock.unlock() }
        guard let p = projects[projectId] else { throw ConstructionError.unknownProject(projectId) }
        return p.budget - spendForLocked(projectId)
    }

    /// Total spend for a project. Caller must hold `lock`.
    private func spendForLocked(_ projectId: String) -> Decimal {
        costs.filter { $0.projectId == projectId }.reduce(Decimal(0)) { $0 + $1.amount }
    }
}

// MARK: - ConstructionDomainContext

/// Static domain-context constants for the construction vertical.
public enum ConstructionDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Construction] Expert construction project management assistant. Help with BOQ preparation, programme of works, site safety plans, NHBRC compliance, subcontractor management, and defect liability. Apply NEC/JBCC contract principles. Compliance: OHS Act, NHBRC Act, CIDB Act, ECSA, National Building Regulations."
    public static let complianceFlags: [String] = ["OHS_Act", "NHBRC_Act", "CIDB_Act", "National_Building_Regs", "POPIA"]
    public static let suggestedTools: [String] = ["project_scheduler", "document_editor", "map", "analytics"]
}

// MARK: - ConstructionCompanionAdapter

/// An `ICompanionSession` decorator that prepends the construction domain system
/// prompt to every conversational call and adds construction helper methods.
/// Port of `CircleAI.Construction.ConstructionCompanionAdapter`. Identity/context/
/// feedback are forwarded to the inner session; proactive events forward through
/// the inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class ConstructionCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(ConstructionDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Construction helpers ──────────────────────────────────────────────────

    /// Draft an OHS-compliant safety plan (C# `DraftSafetyPlanAsync`).
    public func draftSafetyPlan(projectType: String, risks: String) async throws -> String {
        try await inner.agent(
            "Draft an OHS Act-compliant safety plan for a \(projectType) project. Key risks: \(risks). Include risk assessment, control measures, emergency procedures, and competency requirements.")
    }

    /// Prepare a Bill of Quantities structure (C# `PrepareBoqAsync`).
    public func prepareBoq(scope: String) async throws -> String {
        try await inner.agent(
            "Prepare a Bill of Quantities structure for: \(scope). Include trade sections, measurement units, and provisional sums guidance per ASAQS standards.")
    }

    /// Estimate build cost (C# `EstimateCostAsync`).
    public func estimateCost(scope: String, areaM2: Double, finishLevel: String) async throws -> String {
        try await inner.agent(
            "Estimate cost for \(areaM2)m² of \(scope), finish level \(finishLevel). Break by trade, contingency 10%, exclusions.")
    }

    /// Generate a toolbox talk (C# `GenerateSafetyToolboxAsync`).
    public func generateSafetyToolbox(activity: String, siteHazards: String) async throws -> String {
        try await inner.agent(
            "Generate a toolbox talk for '\(activity)' with hazards: \(siteHazards). Format: hazards, controls, PPE, sign-off.")
    }

    /// Sequence the critical path (C# `SequenceCriticalPathAsync`).
    public func sequenceCriticalPath(projectScope: String, durationDays: Int) async throws -> String {
        try await inner.agent(
            "Sequence the critical path for: \(projectScope) in \(durationDays) days. List tasks, dependencies, slack, and 2 risks per phase.")
    }

    /// Draft a snag list (C# `DraftSnagListAsync`).
    public func draftSnagList(area: String, observations: String) async throws -> String {
        try await inner.agent(
            "Draft a snag list for \(area). Observations: \(observations). Order by trade, severity, and access requirement.")
    }
}
