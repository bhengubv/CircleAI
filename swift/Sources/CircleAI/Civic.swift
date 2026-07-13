// Civic.swift
//
// Port of the Civic vertical from src/CircleAI.Civic/CivicPrimitives.cs and the
// static domain-context constants from CivicDomainContext.cs:
//   • CivicIssue, Representative, CivicEvent — domain records
//   • ICivicBoard                           — issues, reps, events
//   • InMemoryCivicBoard                    — deterministic in-memory impl
//   • CivicDomainContext                    — system-prompt snippet + flags
//
// The Companion-facing wrapper (CivicCompanionAdapter) is an ICompanionSession
// decorator that prefixes the civic domain prompt.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`; `string? District` → `String?`.
//   • `Resolve` on an unknown issue throws `.unknownIssue`; it sets the status.
//   • `OpenIssues` returns issues whose Status != "Resolved" (case-insensitive).
//   • `RepsForDistrict` matches District case-insensitively.
//   • `UpcomingEvents` returns events with AtUtc >= now (UTC), ordered ascending.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A reported civic issue.
public struct CivicIssue: Sendable, Equatable, Codable {
    public let issueId: String
    public let category: String
    public let description: String
    public let lat: Double
    public let lon: Double
    public let reportedUtc: Date
    public let status: String

    public init(issueId: String, category: String, description: String, lat: Double, lon: Double, reportedUtc: Date, status: String) {
        self.issueId = issueId
        self.category = category
        self.description = description
        self.lat = lat
        self.lon = lon
        self.reportedUtc = reportedUtc
        self.status = status
    }
}

/// An elected representative.
public struct Representative: Sendable, Equatable, Codable {
    public let repId: String
    public let name: String
    public let office: String
    public let contactEmail: String
    public let district: String?

    public init(repId: String, name: String, office: String, contactEmail: String, district: String?) {
        self.repId = repId
        self.name = name
        self.office = office
        self.contactEmail = contactEmail
        self.district = district
    }
}

/// A civic event.
public struct CivicEvent: Sendable, Equatable, Codable {
    public let eventId: String
    public let title: String
    public let atUtc: Date
    public let location: String
    public let audience: String

    public init(eventId: String, title: String, atUtc: Date, location: String, audience: String) {
        self.eventId = eventId
        self.title = title
        self.atUtc = atUtc
        self.location = location
        self.audience = audience
    }
}

// MARK: - Errors

public enum CivicError: Error, Equatable, CustomStringConvertible {
    case unknownIssue(String)

    public var description: String {
        switch self {
        case .unknownIssue(let id): return "Unknown issue \(id)"
        }
    }
}

// MARK: - Contract

/// Issues, representatives, and events for the civic vertical.
public protocol ICivicBoard: AnyObject, Sendable {
    func report(_ i: CivicIssue)
    func resolve(issueId: String, status: String) throws
    func openIssues() -> [CivicIssue]
    func addRep(_ r: Representative)
    func repsForDistrict(_ district: String) -> [Representative]
    func schedule(_ e: CivicEvent)
    func upcomingEvents() -> [CivicEvent]
}

// MARK: - InMemoryCivicBoard

/// Deterministic in-memory `ICivicBoard`. All state guarded by a single `NSLock`.
public final class InMemoryCivicBoard: ICivicBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var issues: [String: CivicIssue] = [:]
    private var reps: [String: Representative] = [:]
    private var events: [String: CivicEvent] = [:]

    public init() {}

    public func report(_ i: CivicIssue) {
        lock.lock(); defer { lock.unlock() }
        issues[i.issueId] = i
    }

    public func resolve(issueId: String, status: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let i = issues[issueId] else { throw CivicError.unknownIssue(issueId) }
        issues[issueId] = CivicIssue(issueId: i.issueId, category: i.category, description: i.description, lat: i.lat, lon: i.lon, reportedUtc: i.reportedUtc, status: status)
    }

    public func openIssues() -> [CivicIssue] {
        lock.lock(); defer { lock.unlock() }
        return issues.values.filter { $0.status.caseInsensitiveCompare("Resolved") != .orderedSame }
    }

    public func addRep(_ r: Representative) {
        lock.lock(); defer { lock.unlock() }
        reps[r.repId] = r
    }

    public func repsForDistrict(_ district: String) -> [Representative] {
        lock.lock(); defer { lock.unlock() }
        return reps.values.filter { ($0.district ?? "").caseInsensitiveCompare(district) == .orderedSame }
    }

    public func schedule(_ e: CivicEvent) {
        lock.lock(); defer { lock.unlock() }
        events[e.eventId] = e
    }

    public func upcomingEvents() -> [CivicEvent] {
        let now = Date()
        lock.lock(); defer { lock.unlock() }
        return events.values.filter { $0.atUtc >= now }.sorted { $0.atUtc < $1.atUtc }
    }

    /// Number of open (non-"Resolved") issues (matches C#'s `OpenIssueCount`).
    public var openIssueCount: Int {
        lock.lock(); defer { lock.unlock() }
        return issues.values.filter { $0.status.caseInsensitiveCompare("Resolved") != .orderedSame }.count
    }

    /// Issues in a given category (case-insensitive), newest report first.
    /// Matches C#'s `IssuesByCategory` → `OrderByDescending(ReportedUtc)`.
    public func issuesByCategory(_ category: String) -> [CivicIssue] {
        lock.lock(); defer { lock.unlock() }
        return issues.values
            .filter { $0.category.caseInsensitiveCompare(category) == .orderedSame }
            .sorted { $0.reportedUtc > $1.reportedUtc }
    }

    /// Remove a representative by id. Returns true if present (matches C#'s
    /// `RemoveRep` → `TryRemove`).
    @discardableResult
    public func removeRep(_ repId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return reps.removeValue(forKey: repId) != nil
    }

    /// Representatives holding a given office (case-insensitive), ordered by name
    /// (case-insensitive). Matches C#'s `RepsForOffice` →
    /// `OrderBy(Name, OrdinalIgnoreCase)`.
    public func repsForOffice(_ office: String) -> [Representative] {
        lock.lock(); defer { lock.unlock() }
        return reps.values
            .filter { $0.office.caseInsensitiveCompare(office) == .orderedSame }
            .sorted { $0.name.caseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// Events targeting a given audience (case-insensitive), earliest first.
    /// Matches C#'s `EventsForAudience` → `OrderBy(AtUtc)`.
    public func eventsForAudience(_ audience: String) -> [CivicEvent] {
        lock.lock(); defer { lock.unlock() }
        return events.values
            .filter { $0.audience.caseInsensitiveCompare(audience) == .orderedSame }
            .sorted { $0.atUtc < $1.atUtc }
    }

    /// Open-issue counts grouped by category (case-insensitive), largest count
    /// first. Matches C#'s `OpenIssueBreakdown` → `GroupBy(Category,
    /// OrdinalIgnoreCase).OrderByDescending(Count)` (ties keep first-appearance
    /// order of the group key).
    public func openIssueBreakdown() -> [(category: String, count: Int)] {
        lock.lock(); defer { lock.unlock() }
        let open = issues.values.filter { $0.status.caseInsensitiveCompare("Resolved") != .orderedSame }
        var order: [String] = []            // lowercased keys, first-seen order
        var display: [String: String] = [:] // lowercased → first-seen casing
        var counts: [String: Int] = [:]
        for i in open {
            let key = i.category.lowercased()
            if display[key] == nil { display[key] = i.category; order.append(key) }
            counts[key, default: 0] += 1
        }
        return order.enumerated()
            .sorted { a, b in
                let ca = counts[a.element]!, cb = counts[b.element]!
                if ca != cb { return ca > cb }
                return a.offset < b.offset
            }
            .map { (category: display[$0.element]!, count: counts[$0.element]!) }
    }
}

// MARK: - CivicDomainContext

/// Static domain-context constants for the civic vertical.
public enum CivicDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Civic] Expert in civic rights and government services. Help citizens navigate municipal processes, permit applications, public participation, service delivery queries, and constitutional rights. Explain bureaucratic processes in plain language. Compliance: PAJA, PAIA, Constitution of SA, Municipal Systems Act."
    public static let complianceFlags: [String] = ["PAJA", "PAIA", "Constitution_RSA", "Municipal_Systems_Act", "POPIA"]
    public static let suggestedTools: [String] = ["government_portals", "document_editor", "map", "web_search"]
}

// MARK: - CivicCompanionAdapter

/// An `ICompanionSession` decorator that prepends the civic domain system prompt
/// to every conversational call and adds civic helper methods.
/// Port of `CircleAI.Civic.CivicCompanionAdapter`. Identity/context/feedback are
/// forwarded to the inner session; proactive events forward through the inner
/// session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class CivicCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(CivicDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Civic helpers ─────────────────────────────────────────────────────────

    /// Explain a permit process (C# `ExplainPermitProcessAsync`).
    public func explainPermitProcess(permitType: String, municipality: String) async throws -> String {
        try await inner.agent(
            "Explain the application process for a \(permitType) permit in \(municipality). Include required documents, fees, timelines, and escalation steps.")
    }

    /// Draft a formal objection (C# `DraftObjectionAsync`).
    public func draftObjection(issue: String, authority: String) async throws -> String {
        try await inner.agent(
            "Draft a formal objection letter regarding: \(issue). Addressed to: \(authority). Cite relevant rights under PAJA and request a formal response within the prescribed 90 days.")
    }

    /// Draft a petition (C# `DraftPetitionAsync`).
    public func draftPetition(issue: String, targetOffice: String, signatureGoal: Int) async throws -> String {
        try await inner.agent(
            "Draft a clear, factual petition on '\(issue)' to \(targetOffice), targeting \(signatureGoal) signatures. Include problem, ask, evidence, signature ask.")
    }

    /// Log a service failure (C# `LogServiceFailureAsync`).
    public func logServiceFailure(serviceName: String, location: String, failureDescription: String) async throws -> String {
        try await inner.agent(
            "Compose a service-failure report for \(serviceName) at \(location): \(failureDescription). Format for municipal ticketing systems.")
    }

    /// Explain a policy to an audience (C# `ExplainPolicyAsync`).
    public func explainPolicy(policyName: String, audience: String) async throws -> String {
        try await inner.agent(
            "Explain '\(policyName)' to a \(audience). Cover what it does, who's affected, and what to do if it affects you.")
    }

    /// Prepare pointed council questions (C# `PrepareCouncilQuestionsAsync`).
    public func prepareCouncilQuestions(topic: String, questionCount: Int) async throws -> String {
        try await inner.agent(
            "Prepare \(questionCount) pointed questions for council on \(topic). Each should be specific, evidence-based, and require a substantive answer.")
    }
}
