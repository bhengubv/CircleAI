// CommerceIntegrationXeroBoardTests.swift
//
// Exercises the Xero records' Codable round-trips and the deterministic
// behaviour of InMemoryXeroBoard — token storage + expiry (incl. the
// no-tokens=expired case), tenant de-duplication + insertion order, and
// newest-first webhook events with limit. Mirrors
// CircleAI.Commerce.Integration.Xero/XeroPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CommerceIntegrationXeroBoardTests: XCTestCase {

    private func tokens(exp: TimeInterval) -> XeroTokens {
        XeroTokens(accessToken: "at", refreshToken: "rt",
                   expiresAtUtc: Date(timeIntervalSince1970: exp), idToken: "it")
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testTokensCodableRoundTrip() throws {
        let t = tokens(exp: 1000)
        XCTAssertEqual(try JSONDecoder().decode(XeroTokens.self, from: try JSONEncoder().encode(t)), t)
    }

    func testTenantAndEventCodableRoundTrip() throws {
        let ten = XeroTenant(tenantId: "t1", tenantName: "Acme", tenantType: "ORGANISATION")
        XCTAssertEqual(try JSONDecoder().decode(XeroTenant.self, from: try JSONEncoder().encode(ten)), ten)
        let ev = XeroWebhookEvent(tenantId: "t1", resourceType: "INVOICE", resourceId: "r1",
                                  atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(XeroWebhookEvent.self, from: try JSONEncoder().encode(ev)), ev)
    }

    // ── Tokens ───────────────────────────────────────────────────────────────

    func testStoreGetAndExpiry() {
        let b = InMemoryXeroBoard()
        b.storeTokens(userId: "u1", tokens(exp: 1000))
        XCTAssertEqual(b.getTokens(userId: "u1")?.accessToken, "at")
        XCTAssertFalse(b.tokensExpired(userId: "u1", now: Date(timeIntervalSince1970: 999)))
        XCTAssertTrue(b.tokensExpired(userId: "u1", now: Date(timeIntervalSince1970: 1000))) // >= expiry
        XCTAssertTrue(b.tokensExpired(userId: "u1", now: Date(timeIntervalSince1970: 1001)))
    }

    func testUnknownUserTokensAreExpired() {
        let b = InMemoryXeroBoard()
        XCTAssertNil(b.getTokens(userId: "nobody"))
        XCTAssertTrue(b.tokensExpired(userId: "nobody", now: Date(timeIntervalSince1970: 0)))
    }

    // ── Tenants ──────────────────────────────────────────────────────────────

    func testAddTenantDeDuplicatesAndPreservesOrder() {
        let b = InMemoryXeroBoard()
        b.addTenant(userId: "u1", XeroTenant(tenantId: "t1", tenantName: "First", tenantType: "ORG"))
        b.addTenant(userId: "u1", XeroTenant(tenantId: "t2", tenantName: "Second", tenantType: "ORG"))
        b.addTenant(userId: "u1", XeroTenant(tenantId: "t1", tenantName: "Dup", tenantType: "ORG")) // ignored
        let list = b.tenantsFor(userId: "u1")
        XCTAssertEqual(list.map { $0.tenantId }, ["t1", "t2"])
        XCTAssertEqual(list.first?.tenantName, "First") // first-write-wins
    }

    func testTenantsForUnknownUserIsEmpty() {
        XCTAssertTrue(InMemoryXeroBoard().tenantsFor(userId: "nobody").isEmpty)
    }

    // ── Webhooks ─────────────────────────────────────────────────────────────

    func testRecentEventsNewestFirstAndLimit() {
        let b = InMemoryXeroBoard()
        for i in 0..<4 {
            b.recordWebhook(XeroWebhookEvent(tenantId: "t1", resourceType: "INVOICE", resourceId: "r\(i)",
                                             atUtc: Date(timeIntervalSince1970: TimeInterval(i * 100))))
        }
        XCTAssertEqual(b.recentEvents(limit: 2).map { $0.resourceId }, ["r3", "r2"])
        XCTAssertEqual(b.recentEvents().count, 4)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(CommerceIntegrationXeroDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Commerce.Integration.Xero]"))
        XCTAssertTrue(CommerceIntegrationXeroDomainContext.complianceFlags.contains("Xero_Data_Standards"))
    }
}
