// Personal.swift
//
// Calendar, contacts and email - the three places a life assistant has to look,
// and the three places it must not look without being told it may.
//
// Ported from src/CircleAI.Personal.
//
// NAMING: CalendarEvent, EmailMessage and Contact are already taken by the
// CircleAI.Integration and CircleAI.CRM ports, which carry DIFFERENT fields
// (a connector event has a calendarId; this one has an ExternalId and a
// recurrence rule). Swift has no namespaces, so these are PersonalCalendarEvent,
// PersonalEmailMessage and PersonalContact.
//
// EVERY adapter method takes a consent token and checks it FIRST. Not as a
// convention: the guard is the first statement in every implementation here,
// including the ones that do nothing, so a null adapter cannot become the quiet
// way to read somebody mail without permission.

import Foundation

// MARK: - Consent

/// What a token may authorise. Read and write are separate on purpose: being
/// allowed to see a calendar is not being allowed to change it.
public enum ConsentScope: Int, Sendable, Equatable, Hashable, CaseIterable, Codable {
    case calendarRead = 0
    case calendarWrite
    case emailRead
    case emailDraft
    case contactsRead

    public var name: String {
        switch self {
        case .calendarRead: return "CalendarRead"
        case .calendarWrite: return "CalendarWrite"
        case .emailRead: return "EmailRead"
        case .emailDraft: return "EmailDraft"
        case .contactsRead: return "ContactsRead"
        }
    }
}

/// A signed, expiring grant tied to one identity.
public struct UserConsentToken: Sendable, Equatable {
    public let id: UUID
    public let uhidIdentityId: String
    public let scopes: [ConsentScope]
    public let grantedAt: Date
    public let expiresAt: Date
    public let signature: Data

    public init(id: UUID, uhidIdentityId: String, scopes: [ConsentScope],
                grantedAt: Date, expiresAt: Date, signature: Data) {
        self.id = id
        self.uhidIdentityId = uhidIdentityId
        self.scopes = scopes
        self.grantedAt = grantedAt
        self.expiresAt = expiresAt
        self.signature = signature
    }

    /// The scope must be listed AND the token must not have expired. Both, every
    /// time - a token that still lists a scope after it expired grants nothing.
    public func isValid(for scope: ConsentScope, now: Date) -> Bool {
        scopes.contains(scope) && now < expiresAt
    }
}

public enum ConsentError: Error, CustomStringConvertible, Equatable {
    case notGranted(tokenId: UUID, scope: ConsentScope)
    public var description: String {
        switch self {
        case .notGranted(let id, let scope):
            return "Consent token \(id) does not grant scope \(scope.name) or has expired."
        }
    }
}

/// One place the check lives, so no adapter can forget how to spell it.
public enum ConsentGuard {
    public static func require(_ consent: UserConsentToken, _ scope: ConsentScope,
                               now: Date = Date()) throws {
        guard consent.isValid(for: scope, now: now) else {
            throw ConsentError.notGranted(tokenId: consent.id, scope: scope)
        }
    }
}

public enum PersonalAdapterError: Error, CustomStringConvertible, Equatable {
    case notBound(String)
    public var description: String {
        switch self {
        case .notBound(let what):
            return "The null adapter cannot \(what). Bind a concrete adapter " +
                   "(Google, Microsoft Graph, iOS EventKit, ...)."
        }
    }
}

// MARK: - Calendar

public struct PersonalCalendarEvent: Sendable, Equatable {
    public let id: UUID
    public let externalId: String
    public let title: String
    public let eventDescription: String?
    public let startUtc: Date
    public let endUtc: Date
    public let location: String?
    public let attendeeEmails: [String]
    public let isAllDay: Bool
    public let recurrenceRule: String?

    public init(id: UUID = UUID(), externalId: String, title: String, description: String? = nil,
                startUtc: Date, endUtc: Date, location: String? = nil,
                attendeeEmails: [String] = [], isAllDay: Bool = false,
                recurrenceRule: String? = nil) {
        self.id = id
        self.externalId = externalId
        self.title = title
        self.eventDescription = description
        self.startUtc = startUtc
        self.endUtc = endUtc
        self.location = location
        self.attendeeEmails = attendeeEmails
        self.isAllDay = isAllDay
        self.recurrenceRule = recurrenceRule
    }
}

public protocol ICalendarAdapter: Sendable {
    func listEvents(from: Date, to: Date, consent: UserConsentToken) async throws -> [PersonalCalendarEvent]
    func createEvent(_ event: PersonalCalendarEvent, consent: UserConsentToken) async throws -> PersonalCalendarEvent
    func updateEvent(_ event: PersonalCalendarEvent, consent: UserConsentToken) async throws -> PersonalCalendarEvent
    func deleteEvent(id: UUID, consent: UserConsentToken) async throws
}

/// Nothing is bound. Reads come back empty; writes refuse and say what to bind.
/// Both still check consent first - a null adapter must not become the quiet
/// way to reach somebody data without permission.
public struct NullCalendarAdapter: ICalendarAdapter {
    public static let instance = NullCalendarAdapter()
    public init() {}

    public func listEvents(from: Date, to: Date, consent: UserConsentToken) async throws -> [PersonalCalendarEvent] {
        try ConsentGuard.require(consent, .calendarRead)
        return []
    }
    public func createEvent(_ event: PersonalCalendarEvent, consent: UserConsentToken) async throws -> PersonalCalendarEvent {
        try ConsentGuard.require(consent, .calendarWrite)
        throw PersonalAdapterError.notBound("create events")
    }
    public func updateEvent(_ event: PersonalCalendarEvent, consent: UserConsentToken) async throws -> PersonalCalendarEvent {
        try ConsentGuard.require(consent, .calendarWrite)
        throw PersonalAdapterError.notBound("update events")
    }
    public func deleteEvent(id: UUID, consent: UserConsentToken) async throws {
        try ConsentGuard.require(consent, .calendarWrite)
        throw PersonalAdapterError.notBound("delete events")
    }
}

// MARK: - Contacts

public struct PersonalContact: Sendable, Equatable {
    public let id: UUID
    public let externalId: String
    public let displayName: String
    public let emails: [String]
    public let phoneNumbers: [String]
    public let relationship: String?
    public let lastInteractionAt: Date

    public init(id: UUID = UUID(), externalId: String, displayName: String,
                emails: [String] = [], phoneNumbers: [String] = [],
                relationship: String? = nil, lastInteractionAt: Date) {
        self.id = id
        self.externalId = externalId
        self.displayName = displayName
        self.emails = emails
        self.phoneNumbers = phoneNumbers
        self.relationship = relationship
        self.lastInteractionAt = lastInteractionAt
    }
}

public protocol IContactsAdapter: Sendable {
    func search(_ query: String, consent: UserConsentToken) async throws -> [PersonalContact]
    func getByExternalId(_ externalId: String, consent: UserConsentToken) async throws -> PersonalContact?
}

public struct NullContactsAdapter: IContactsAdapter {
    public static let instance = NullContactsAdapter()
    public init() {}

    public func search(_ query: String, consent: UserConsentToken) async throws -> [PersonalContact] {
        try ConsentGuard.require(consent, .contactsRead)
        return []
    }
    public func getByExternalId(_ externalId: String, consent: UserConsentToken) async throws -> PersonalContact? {
        try ConsentGuard.require(consent, .contactsRead)
        return nil
    }
}

// MARK: - Email

public struct PersonalEmailMessage: Sendable, Equatable {
    public let id: UUID
    public let externalId: String
    public let from: String
    public let to: [String]
    public let cc: [String]
    public let subject: String
    public let bodyPlain: String
    public let receivedAt: Date
    public let isUnread: Bool
    public let labels: [String]

    public init(id: UUID = UUID(), externalId: String, from: String, to: [String] = [],
                cc: [String] = [], subject: String, bodyPlain: String, receivedAt: Date,
                isUnread: Bool = false, labels: [String] = []) {
        self.id = id
        self.externalId = externalId
        self.from = from
        self.to = to
        self.cc = cc
        self.subject = subject
        self.bodyPlain = bodyPlain
        self.receivedAt = receivedAt
        self.isUnread = isUnread
        self.labels = labels
    }
}

public protocol IEmailAdapter: Sendable {
    func listRecent(count: Int, consent: UserConsentToken) async throws -> [PersonalEmailMessage]
    func getById(_ externalId: String, consent: UserConsentToken) async throws -> PersonalEmailMessage?
    /// DRAFTS a reply. It does not send one - sending is the user job, and no
    /// scope in this module authorises it.
    func draftReply(toExternalId: String, bodyPlain: String,
                    consent: UserConsentToken) async throws -> UUID
}

public struct NullEmailAdapter: IEmailAdapter {
    public static let instance = NullEmailAdapter()
    public init() {}

    public func listRecent(count: Int, consent: UserConsentToken) async throws -> [PersonalEmailMessage] {
        try ConsentGuard.require(consent, .emailRead)
        return []
    }
    public func getById(_ externalId: String, consent: UserConsentToken) async throws -> PersonalEmailMessage? {
        try ConsentGuard.require(consent, .emailRead)
        return nil
    }
    public func draftReply(toExternalId: String, bodyPlain: String,
                           consent: UserConsentToken) async throws -> UUID {
        try ConsentGuard.require(consent, .emailDraft)
        return UUID()
    }
}

// MARK: - Domain

public enum PersonalDomainContext {
    public static let systemPromptSnippet =
        "[DOMAIN: Personal] You are Circle, a personal life assistant. Help with daily planning, "
        + "goal setting, decision making, life admin (insurance, subscriptions, tasks), journaling "
        + "prompts, and personal organisation. Be warm, encouraging, and non-judgmental. Remember "
        + "context across conversations. Compliance: POPIA."

    public static let complianceFlags = ["POPIA"]

    public static let suggestedTools = ["calendar", "task_manager", "document_editor", "web_search"]
}
