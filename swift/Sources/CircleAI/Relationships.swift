// Relationships.swift
//
// Port of the Relationships vertical from
// src/CircleAI.Relationships/RelationshipsPrimitives.cs and the static
// domain-context constants from RelationshipsDomainContext.cs:
//   • PersonContact, ImportantDate, ContactEvent — domain records
//   • IRelationshipsBoard                        — contacts, dates, touchpoints
//   • InMemoryRelationshipsBoard                 — deterministic in-memory impl
//   • RelationshipsDomainContext                 — system-prompt snippet + flags
//
// The Companion-facing wrapper (RelationshipsCompanionAdapter) is an
// ICompanionSession decorator that prefixes the relationships domain prompt.
//
// Porting notes:
//   • `DateTime` → `Date`; `DateTimeOffset` → `Date`; `string? Notes` → `String?`.
//   • `Contacts` is ordered ascending by Name.
//   • `UpcomingThisMonth` returns important dates whose month == the current UTC
//     month, ordered ascending by day-of-month.
//   • `LastContact` returns the most recent touchpoint time for a contact (nil
//     if none). `NotContactedSince(cutoff)` returns contacts whose last contact
//     is nil or earlier than cutoff.
//   • All state guarded by a single `NSLock` (the last-contact lookup used by
//     `NotContactedSince` is a non-locking private helper to avoid re-entrancy).

import Foundation

// MARK: - Records

/// A personal contact.
public struct PersonContact: Sendable, Equatable, Codable {
    public let contactId: String
    public let name: String
    public let relationship: String
    public let notes: String?

    public init(contactId: String, name: String, relationship: String, notes: String?) {
        self.contactId = contactId
        self.name = name
        self.relationship = relationship
        self.notes = notes
    }
}

/// An important date for a contact.
public struct ImportantDate: Sendable, Equatable, Codable {
    public let dateId: String
    public let contactId: String
    public let kind: String
    public let date: Date

    public init(dateId: String, contactId: String, kind: String, date: Date) {
        self.dateId = dateId
        self.contactId = contactId
        self.kind = kind
        self.date = date
    }
}

/// A recorded touchpoint with a contact.
public struct ContactEvent: Sendable, Equatable, Codable {
    public let contactId: String
    public let kind: String
    public let atUtc: Date
    public let note: String?

    public init(contactId: String, kind: String, atUtc: Date, note: String?) {
        self.contactId = contactId
        self.kind = kind
        self.atUtc = atUtc
        self.note = note
    }
}

// MARK: - Contract

/// Contacts, important dates, and touchpoints for the relationships vertical.
public protocol IRelationshipsBoard: AnyObject, Sendable {
    func addContact(_ c: PersonContact)
    func getContact(_ id: String) -> PersonContact?
    var contacts: [PersonContact] { get }
    func addImportantDate(_ d: ImportantDate)
    func upcomingThisMonth() -> [ImportantDate]
    func recordTouchpoint(_ e: ContactEvent)
    func lastContact(contactId: String) -> Date?
    func notContactedSince(cutoff: Date) -> [PersonContact]
}

// MARK: - InMemoryRelationshipsBoard

/// Deterministic in-memory `IRelationshipsBoard`. All state guarded by a single `NSLock`.
public final class InMemoryRelationshipsBoard: IRelationshipsBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var contactsMap: [String: PersonContact] = [:]
    private var dates: [String: ImportantDate] = [:]
    private var events: [ContactEvent] = []

    public init() {}

    public func addContact(_ c: PersonContact) {
        lock.lock(); defer { lock.unlock() }
        contactsMap[c.contactId] = c
    }

    public func getContact(_ id: String) -> PersonContact? {
        lock.lock(); defer { lock.unlock() }
        return contactsMap[id]
    }

    public var contacts: [PersonContact] {
        lock.lock(); defer { lock.unlock() }
        return contactsMap.values.sorted { $0.name < $1.name }
    }

    public func addImportantDate(_ d: ImportantDate) {
        lock.lock(); defer { lock.unlock() }
        dates[d.dateId] = d
    }

    public func upcomingThisMonth() -> [ImportantDate] {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let month = cal.component(.month, from: Date())
        lock.lock(); defer { lock.unlock() }
        return dates.values
            .filter { cal.component(.month, from: $0.date) == month }
            .sorted { cal.component(.day, from: $0.date) < cal.component(.day, from: $1.date) }
    }

    public func recordTouchpoint(_ e: ContactEvent) {
        lock.lock(); defer { lock.unlock() }
        events.append(e)
    }

    public func lastContact(contactId: String) -> Date? {
        lock.lock(); defer { lock.unlock() }
        return lastContactLocked(contactId)
    }

    public func notContactedSince(cutoff: Date) -> [PersonContact] {
        lock.lock(); defer { lock.unlock() }
        return contactsMap.values.filter { c in
            let last = lastContactLocked(c.contactId)
            return last == nil || last! < cutoff
        }
    }

    /// Most recent touchpoint time for a contact. Caller must hold `lock`.
    private func lastContactLocked(_ contactId: String) -> Date? {
        events.filter { $0.contactId == contactId }.map { $0.atUtc }.max()
    }
}

// MARK: - RelationshipsDomainContext

/// Static domain-context constants for the relationships vertical.
public enum RelationshipsDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Relationships] Empathetic relationship support companion. Help with communication strategies, conflict resolution (NVC principles), relationship goal-setting, and self-reflection prompts. Non-judgmental, no-advice-without-consent approach. Not a therapy service. Compliance: POPIA."
    public static let complianceFlags: [String] = ["POPIA", "Not_Therapy"]
    public static let suggestedTools: [String] = ["journal", "mood_tracker", "calendar"]
}

// MARK: - RelationshipsCompanionAdapter

/// An `ICompanionSession` decorator that prepends the relationships domain
/// system prompt to every conversational call and adds relationship helper
/// methods. Port of `CircleAI.Relationships.RelationshipsCompanionAdapter`.
/// Identity/context/feedback are forwarded to the inner session; proactive
/// events forward through the inner session's `proactiveEvents` stream (the
/// Swift protocol has no disposal).
public final class RelationshipsCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(RelationshipsDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Relationships helpers ─────────────────────────────────────────────────

    /// Guide conflict resolution via NVC (C# `GuideConflictResolutionAsync`).
    public func guideConflictResolution(situation: String) async throws -> String {
        try await inner.agent(
            "Guide me through resolving this conflict using Non-Violent Communication (NVC):\n\(situation)\nHelp me identify observations, feelings, needs, and requests.")
    }

    /// Prepare for a difficult conversation (C# `DraftDifficultConversationAsync`).
    public func draftDifficultConversation(topic: String, relationship: String) async throws -> String {
        try await inner.agent(
            "Help me prepare for a difficult conversation about \(topic) with my \(relationship). Draft key points using assertive but empathetic language.")
    }

    /// Plan a check-in (C# `PlanCheckInAsync`).
    public func planCheckIn(relationship: String, lastTouch: String, occasion: String) async throws -> String {
        try await inner.agent(
            "Plan a check-in with \(relationship), last touched \(lastTouch). Occasion: \(occasion). Suggest channel, opener, generous question.")
    }

    /// Draft a meaningful message (C# `DraftMeaningfulMessageAsync`).
    public func draftMeaningfulMessage(recipient: String, moment: String) async throws -> String {
        try await inner.agent(
            "Draft a heartfelt message to \(recipient) for \(moment). Specific, not generic; refer to shared history.")
    }

    /// Resolve tension toward an outcome (C# `ResolveTensionAsync`).
    public func resolveTension(conflictSummary: String, desiredOutcome: String) async throws -> String {
        try await inner.agent(
            "Help resolve tension: \(conflictSummary). Desired outcome: \(desiredOutcome). NVC-style script + likely responses.")
    }

    /// Prep for an important date (C# `RememberImportantDateAsync`).
    public func rememberImportantDate(personName: String, date: String, history: String) async throws -> String {
        try await inner.agent(
            "Prep for \(personName)'s important date (\(date)). History: \(history). Suggest gift, message, gesture.")
    }
}
