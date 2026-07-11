// CommerceIntegrationXero.swift
//
// Port of the Commerce.Integration.Xero vertical from
// src/CircleAI.Commerce.Integration.Xero/XeroPrimitives.cs and the static
// domain-context constants from CommerceIntegrationXeroDomainContext.cs:
//   • XeroTokens, XeroTenant, XeroWebhookEvent — domain records
//   • IXeroBoard                               — token store / tenants / webhooks
//   • InMemoryXeroBoard                        — deterministic in-memory impl
//   • CommerceIntegrationXeroDomainContext     — system-prompt snippet + flags
//
// The Companion-facing wrapper (CommerceIntegrationXeroCompanionAdapter) is
// intentionally NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `TokensExpired` returns `true` when no tokens are stored for the user;
//     otherwise `now >= expiresAtUtc`.
//   • `AddTenant` de-duplicates by `tenantId` (first-write-wins per id) and
//     preserves insertion order. `TenantsFor` returns the ordered list (or
//     empty). `RecentEvents` returns the most-recent `limit` descending by time.

import Foundation

// MARK: - Records

/// Xero OAuth token set for a user.
public struct XeroTokens: Sendable, Equatable, Codable {
    /// OAuth access token.
    public let accessToken: String
    /// OAuth refresh token.
    public let refreshToken: String
    /// Access-token expiry (UTC).
    public let expiresAtUtc: Date
    /// OpenID id token.
    public let idToken: String

    public init(accessToken: String, refreshToken: String, expiresAtUtc: Date, idToken: String) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAtUtc = expiresAtUtc
        self.idToken = idToken
    }
}

/// A Xero tenant (organisation) connected to a user.
public struct XeroTenant: Sendable, Equatable, Codable {
    /// Tenant identifier.
    public let tenantId: String
    /// Tenant (organisation) name.
    public let tenantName: String
    /// Tenant type (e.g. "ORGANISATION").
    public let tenantType: String

    public init(tenantId: String, tenantName: String, tenantType: String) {
        self.tenantId = tenantId
        self.tenantName = tenantName
        self.tenantType = tenantType
    }
}

/// A Xero webhook event.
public struct XeroWebhookEvent: Sendable, Equatable, Codable {
    /// Tenant the event belongs to.
    public let tenantId: String
    /// Resource type (e.g. "INVOICE").
    public let resourceType: String
    /// Resource identifier.
    public let resourceId: String
    /// UTC timestamp.
    public let atUtc: Date

    public init(tenantId: String, resourceType: String, resourceId: String, atUtc: Date) {
        self.tenantId = tenantId
        self.resourceType = resourceType
        self.resourceId = resourceId
        self.atUtc = atUtc
    }
}

// MARK: - IXeroBoard

/// Xero token storage, tenant tracking, and webhook recording. A synchronous
/// contract — implementations are expected to be thread-safe.
public protocol IXeroBoard: AnyObject, Sendable {
    /// Stores (or replaces, by `userId`) a token set.
    func storeTokens(userId: String, _ t: XeroTokens)
    /// Returns the stored tokens for `userId`, or `nil`.
    func getTokens(userId: String) -> XeroTokens?
    /// Whether the tokens for `userId` are expired as of `now` (true if none).
    func tokensExpired(userId: String, now: Date) -> Bool
    /// Adds a tenant for `userId`, de-duplicated by `tenantId`.
    func addTenant(userId: String, _ t: XeroTenant)
    /// Tenants connected for `userId`, in insertion order.
    func tenantsFor(userId: String) -> [XeroTenant]
    /// Records a webhook event.
    func recordWebhook(_ e: XeroWebhookEvent)
    /// Up to `limit` most-recent events, newest first.
    func recentEvents(limit: Int) -> [XeroWebhookEvent]
}

public extension IXeroBoard {
    /// Overload matching the C# default `limit = 20`.
    func recentEvents() -> [XeroWebhookEvent] { recentEvents(limit: 20) }
}

// MARK: - InMemoryXeroBoard

/// Deterministic in-memory `IXeroBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryXeroBoard: IXeroBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var tokens: [String: XeroTokens] = [:]
    private var tenants: [String: [XeroTenant]] = [:]
    private var events: [XeroWebhookEvent] = []

    public init() {}

    public func storeTokens(userId: String, _ t: XeroTokens) {
        lock.lock(); defer { lock.unlock() }
        tokens[userId] = t
    }

    public func getTokens(userId: String) -> XeroTokens? {
        lock.lock(); defer { lock.unlock() }
        return tokens[userId]
    }

    public func tokensExpired(userId: String, now: Date) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let t = tokens[userId] else { return true }
        return now >= t.expiresAtUtc
    }

    public func addTenant(userId: String, _ t: XeroTenant) {
        lock.lock(); defer { lock.unlock() }
        var list = tenants[userId] ?? []
        if !list.contains(where: { $0.tenantId == t.tenantId }) {
            list.append(t)
        }
        tenants[userId] = list
    }

    public func tenantsFor(userId: String) -> [XeroTenant] {
        lock.lock(); defer { lock.unlock() }
        return tenants[userId] ?? []
    }

    public func recordWebhook(_ e: XeroWebhookEvent) {
        lock.lock(); defer { lock.unlock() }
        events.append(e)
    }

    public func recentEvents(limit: Int) -> [XeroWebhookEvent] {
        lock.lock(); defer { lock.unlock() }
        return Array(events.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }
}

// MARK: - CommerceIntegrationXeroDomainContext

/// Static domain-context constants for the Xero integration vertical. Mirrors
/// `CommerceIntegrationXeroDomainContext` in
/// CommerceIntegrationXeroDomainContext.cs.
public enum CommerceIntegrationXeroDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Commerce.Integration.Xero] You are a Xero accounting platform expert. Help with Xero chart of accounts, invoice creation, bank feeds, reconciliation workflows, Xero reporting, and API integration troubleshooting. Reference Xero HQ documentation for accuracy. Compliance: SARS, IFRS for SMEs, Xero data handling standards."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["SARS", "IFRS", "Xero_Data_Standards", "POPIA"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["xero_api", "spreadsheet", "document_editor"]
}
