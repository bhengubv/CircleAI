// Community.swift
//
// Port of the Community vertical from
// src/CircleAI.Community/CommunityPrimitives.cs and the static domain-context
// constants from CommunityDomainContext.cs:
//   • CommunityGroup, Announcement, VolunteerOpportunity — domain records
//   • ICommunityBoard                                    — groups, announcements, opportunities
//   • InMemoryCommunityBoard                             — deterministic in-memory impl
//   • CommunityDomainContext                             — system-prompt snippet + flags
//
// The Companion-facing wrapper (CommunityCompanionAdapter) is an
// ICompanionSession decorator that prefixes the community domain prompt.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `GroupsForMember` returns groups whose MemberIds contain the member.
//   • `AnnouncementsFor(groupId, limit)` orders descending by AtUtc, takes limit
//     (default 20).
//   • `Opportunities` returns opportunities with WhenUtc >= now (UTC), ordered
//     ascending. All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A community group.
public struct CommunityGroup: Sendable, Equatable, Codable {
    public let groupId: String
    public let name: String
    public let purpose: String
    public let memberIds: [String]

    public init(groupId: String, name: String, purpose: String, memberIds: [String]) {
        self.groupId = groupId
        self.name = name
        self.purpose = purpose
        self.memberIds = memberIds
    }
}

/// A group announcement.
public struct Announcement: Sendable, Equatable, Codable {
    public let announcementId: String
    public let groupId: String
    public let title: String
    public let body: String
    public let atUtc: Date

    public init(announcementId: String, groupId: String, title: String, body: String, atUtc: Date) {
        self.announcementId = announcementId
        self.groupId = groupId
        self.title = title
        self.body = body
        self.atUtc = atUtc
    }
}

/// A volunteer opportunity.
public struct VolunteerOpportunity: Sendable, Equatable, Codable {
    public let oppId: String
    public let groupId: String
    public let description: String
    public let volunteersNeeded: Int
    public let whenUtc: Date

    public init(oppId: String, groupId: String, description: String, volunteersNeeded: Int, whenUtc: Date) {
        self.oppId = oppId
        self.groupId = groupId
        self.description = description
        self.volunteersNeeded = volunteersNeeded
        self.whenUtc = whenUtc
    }
}

// MARK: - Contract

/// Groups, announcements, and volunteer opportunities for the community vertical.
public protocol ICommunityBoard: AnyObject, Sendable {
    func create(_ g: CommunityGroup)
    func getGroup(_ id: String) -> CommunityGroup?
    func groupsForMember(memberId: String) -> [CommunityGroup]
    func post(_ a: Announcement)
    func announcementsFor(groupId: String, limit: Int) -> [Announcement]
    func list(_ o: VolunteerOpportunity)
    func opportunities() -> [VolunteerOpportunity]
}

public extension ICommunityBoard {
    /// Convenience overload mirroring the C# default `limit = 20`.
    func announcementsFor(groupId: String) -> [Announcement] { announcementsFor(groupId: groupId, limit: 20) }
}

// MARK: - InMemoryCommunityBoard

/// Deterministic in-memory `ICommunityBoard`. All state guarded by a single `NSLock`.
public final class InMemoryCommunityBoard: ICommunityBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var groups: [String: CommunityGroup] = [:]
    private var annc: [Announcement] = []
    private var opps: [String: VolunteerOpportunity] = [:]

    public init() {}

    public func create(_ g: CommunityGroup) {
        lock.lock(); defer { lock.unlock() }
        groups[g.groupId] = g
    }

    public func getGroup(_ id: String) -> CommunityGroup? {
        lock.lock(); defer { lock.unlock() }
        return groups[id]
    }

    public func groupsForMember(memberId: String) -> [CommunityGroup] {
        lock.lock(); defer { lock.unlock() }
        return groups.values.filter { $0.memberIds.contains(memberId) }
    }

    public func post(_ a: Announcement) {
        lock.lock(); defer { lock.unlock() }
        annc.append(a)
    }

    public func announcementsFor(groupId: String, limit: Int = 20) -> [Announcement] {
        lock.lock(); defer { lock.unlock() }
        return Array(annc.filter { $0.groupId == groupId }.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }

    public func list(_ o: VolunteerOpportunity) {
        lock.lock(); defer { lock.unlock() }
        opps[o.oppId] = o
    }

    public func opportunities() -> [VolunteerOpportunity] {
        let now = Date()
        lock.lock(); defer { lock.unlock() }
        return opps.values.filter { $0.whenUtc >= now }.sorted { $0.whenUtc < $1.whenUtc }
    }

    /// Number of groups (matches C#'s `GroupCount`).
    public var groupCount: Int {
        lock.lock(); defer { lock.unlock() }
        return groups.count
    }

    /// Remove a group by id. Returns true if present (matches C#'s `RemoveGroup`
    /// → `TryRemove`).
    @discardableResult
    public func removeGroup(_ groupId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return groups.removeValue(forKey: groupId) != nil
    }

    /// Add a member to a group. False if the group is unknown or the member is
    /// already in it; otherwise appends and returns true (matches C#'s
    /// `AddMember`).
    @discardableResult
    public func addMember(groupId: String, memberId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let g = groups[groupId] else { return false }
        if g.memberIds.contains(memberId) { return false }
        groups[groupId] = CommunityGroup(
            groupId: g.groupId, name: g.name, purpose: g.purpose,
            memberIds: g.memberIds + [memberId])
        return true
    }

    /// Remove a member from a group. False if the group is unknown or the member
    /// is not in it; otherwise removes and returns true (matches C#'s
    /// `RemoveMember`).
    @discardableResult
    public func removeMember(groupId: String, memberId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let g = groups[groupId] else { return false }
        if !g.memberIds.contains(memberId) { return false }
        groups[groupId] = CommunityGroup(
            groupId: g.groupId, name: g.name, purpose: g.purpose,
            memberIds: g.memberIds.filter { $0 != memberId })
        return true
    }

    /// A group's volunteer opportunities (groupId ordinal), earliest first — ALL
    /// of them, no time filter. Matches C#'s `OpportunitiesForGroup` →
    /// `OrderBy(WhenUtc)`.
    public func opportunitiesForGroup(_ groupId: String) -> [VolunteerOpportunity] {
        lock.lock(); defer { lock.unlock() }
        return opps.values
            .filter { $0.groupId == groupId }
            .sorted { $0.whenUtc < $1.whenUtc }
    }

    /// Total volunteers needed across FUTURE opportunities only (mirrors C#'s
    /// `TotalVolunteersNeeded` → `Opportunities().Sum(...)`, where
    /// `Opportunities()` is future-filtered).
    public func totalVolunteersNeeded() -> Int {
        let now = Date()
        lock.lock(); defer { lock.unlock() }
        return opps.values.filter { $0.whenUtc >= now }.reduce(0) { $0 + $1.volunteersNeeded }
    }
}

// MARK: - CommunityDomainContext

/// Static domain-context constants for the community vertical.
public enum CommunityDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Community] Community organising and engagement assistant. Help with community event planning, volunteer coordination, advocacy letter writing, fundraising strategies, and neighbourhood communication. Empower grassroots action. Compliance: NPO Act, POPIA, Fundraising Act."
    public static let complianceFlags: [String] = ["NPO_Act", "Fundraising_Act", "POPIA"]
    public static let suggestedTools: [String] = ["event_manager", "document_editor", "communication_tools", "volunteer_tracker"]
}

// MARK: - CommunityCompanionAdapter

/// An `ICompanionSession` decorator that prepends the community domain system
/// prompt to every conversational call and adds community helper methods.
/// Port of `CircleAI.Community.CommunityCompanionAdapter`. Identity/context/
/// feedback are forwarded to the inner session; proactive events forward through
/// the inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class CommunityCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(CommunityDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Community helpers ─────────────────────────────────────────────────────

    /// Plan a community event (C# `PlanCommunityEventAsync`).
    public func planCommunityEvent(eventType: String, size: String, budget: String) async throws -> String {
        try await inner.agent(
            "Plan a community \(eventType) for \(size) people. Budget: \(budget). Include logistics checklist, volunteer roles, publicity plan, and risk management.")
    }

    /// Write an advocacy letter (C# `WriteAdvocacyLetterAsync`).
    public func writeAdvocacyLetter(issue: String, authority: String) async throws -> String {
        try await inner.agent(
            "Write a compelling advocacy letter about \(issue) to \(authority). Include evidence, community impact, and specific ask.")
    }

    /// Write a community announcement (C# `WriteAnnouncementAsync`).
    public func writeAnnouncement(groupName: String, subject: String, callToAction: String) async throws -> String {
        try await inner.agent(
            "Write a community announcement for \(groupName) about '\(subject)'. CTA: \(callToAction). Warm, concise, 80 words.")
    }

    /// Draft a conflict-mediation opener (C# `DraftConflictMediationOpenerAsync`).
    public func draftConflictMediationOpener(conflictSummary: String, partiesInvolved: String) async throws -> String {
        try await inner.agent(
            "Draft a mediator-style opener for: \(conflictSummary) involving \(partiesInvolved). Acknowledge feelings, set ground rules, propose next step.")
    }

    /// Design a volunteer campaign (C# `DesignVolunteerCampaignAsync`).
    public func designVolunteerCampaign(need: String, peopleNeeded: Int, when: String) async throws -> String {
        try await inner.agent(
            "Design a volunteer drive: need \(need), \(peopleNeeded) people, \(when). Cover signup channel, shift design, recognition, retention.")
    }

    /// Write a community newsletter (C# `WriteCommunityNewsletterAsync`).
    public func writeCommunityNewsletter(highlights: String, upcoming: String) async throws -> String {
        try await inner.agent(
            "Write a 200-word community newsletter. Highlights: \(highlights). Upcoming: \(upcoming). Friendly, scan-friendly.")
    }
}
