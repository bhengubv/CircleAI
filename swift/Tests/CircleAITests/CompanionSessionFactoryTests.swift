// CompanionSessionFactoryTests.swift
//
// Verifies CompanionSessionFactory (CompanionSessionFactory.swift): builds a
// working CompanionSession, resolves display name / preferred language from the
// injected IIdentityProvider when present, and falls back to the identityId when
// no provider (or no current identity) is available.

import XCTest
@testable import CircleAI

final class CompanionSessionFactoryTests: XCTestCase {

    final class StubGenerator: IChatGenerator, @unchecked Sendable {
        func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String { "ok" }
        func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
            AsyncStream { $0.finish() }
        }
    }

    final class FixedIdentityProvider: IIdentityProvider, @unchecked Sendable {
        let identity: CircleIdentity?
        init(_ identity: CircleIdentity?) { self.identity = identity }
        func getCurrentIdentity() async throws -> CircleIdentity? { identity }
        func isAuthenticated() async throws -> Bool { identity != nil }
        func createIdentity(displayName: String, preferredLanguage: String?) async throws -> CircleIdentity {
            CircleIdentity(identityId: "new", displayName: displayName, preferredLanguage: preferredLanguage,
                           tier: .anonymous, deviceIds: [], createdAt: Date(), lastSeenAt: Date())
        }
    }

    private func makeFactory(identity: IIdentityProvider? = nil) -> CompanionSessionFactory {
        let episodic = InMemoryEpisodicStore()
        let recall = FusedRecall(episodic: episodic, graph: nil)
        return CompanionSessionFactory(
            generator: StubGenerator(), episodic: episodic, recall: recall, identity: identity)
    }

    func testCreatesUsableSession() async throws {
        let factory = makeFactory()
        let session = try await factory.create(identityId: "u1", interface: .mobile)
        XCTAssertEqual(session.identityId, "u1")
        XCTAssertEqual(session.interface, .mobile)
        XCTAssertFalse(session.sessionId.isEmpty)
        // A round-trip works against the stub generator.
        let reply = try await session.send("hello")
        XCTAssertEqual(reply, "ok")
    }

    func testFallsBackToIdentityIdWithoutProvider() async throws {
        let factory = makeFactory(identity: nil)
        let session = try await factory.create(identityId: "u42", interface: .web)
        XCTAssertEqual(session.getContext().displayName, "u42",
                       "no identity provider → display name defaults to the id")
    }

    func testResolvesDisplayNameFromProvider() async throws {
        let identity = CircleIdentity(
            identityId: "u1", displayName: "Thabo", preferredLanguage: "zu",
            tier: .verified, deviceIds: [], createdAt: Date(), lastSeenAt: Date())
        let factory = makeFactory(identity: FixedIdentityProvider(identity))
        let session = try await factory.create(identityId: "u1", interface: .desktop)
        let ctx = session.getContext()
        XCTAssertEqual(ctx.displayName, "Thabo")
        XCTAssertEqual(ctx.preferredLanguage, "zu")
    }

    func testProviderWithNoCurrentIdentityFallsBack() async throws {
        let factory = makeFactory(identity: FixedIdentityProvider(nil))
        let session = try await factory.create(identityId: "u7", interface: .ambient)
        XCTAssertEqual(session.getContext().displayName, "u7")
    }

    func testEachSessionGetsUniqueId() async throws {
        let factory = makeFactory()
        let a = try await factory.create(identityId: "u", interface: .mobile)
        let b = try await factory.create(identityId: "u", interface: .mobile)
        XCTAssertNotEqual(a.sessionId, b.sessionId)
    }
}
