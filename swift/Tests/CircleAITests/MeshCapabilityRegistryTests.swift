// MeshCapabilityRegistryTests.swift
//
// Validates the CircleAI.AetherNet mesh-capability port
// (MeshCapabilityRegistry.swift): upsert-replaces-per-peer, idempotent remove,
// stale filtering, Find (model match case-insensitive + min-KV filter + spare-
// budget-descending order), the Null broadcaster no-op, and the loopback
// broadcaster's real registry effect.

import XCTest
import Foundation
@testable import CircleAI

final class MeshCapabilityRegistryTests: XCTestCase {

    private func ad(
        _ peer: String, model: String = "Qwen3-1.7B-MNN", kv: Int = 2048,
        tier: DeviceTier = .phone, ctx: Int = 4096, at: Date, latency: Int? = nil
    ) -> MeshCapabilityAdvertisement {
        MeshCapabilityAdvertisement(
            peerId: peer, modelId: model, freeKvTokens: kv, tier: tier,
            contextWindowTokens: ctx, advertisedAtUtc: at, latencyHintMs: latency)
    }

    // ── Upsert / replace / list ───────────────────────────────────────────────

    func testUpsertReplacesPerPeer() async throws {
        let reg = InMemoryMeshCapabilityRegistry()
        let now = Date()
        try await reg.upsert(ad("p1", kv: 1000, at: now))
        try await reg.upsert(ad("p1", kv: 4000, at: now)) // replaces, does not append
        let all = reg.list()
        XCTAssertEqual(all.count, 1)
        XCTAssertEqual(all.first?.freeKvTokens, 4000)
    }

    func testUpsertRejectsBlankPeerId() async {
        let reg = InMemoryMeshCapabilityRegistry()
        do {
            try await reg.upsert(ad("   ", at: Date()))
            XCTFail("expected invalidPeerId")
        } catch let e as MeshCapabilityError {
            XCTAssertEqual(e, .invalidPeerId)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    func testListEmptyRegistryIsEmpty() {
        let reg = InMemoryMeshCapabilityRegistry()
        XCTAssertTrue(reg.list().isEmpty)
        XCTAssertTrue(reg.list(staleAfter: 60).isEmpty)
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    func testRemoveIsIdempotent() async throws {
        let reg = InMemoryMeshCapabilityRegistry()
        try await reg.upsert(ad("p1", at: Date()))
        let first = try await reg.remove(peerId: "p1")
        let second = try await reg.remove(peerId: "p1")
        XCTAssertTrue(first)
        XCTAssertFalse(second)
        XCTAssertTrue(reg.list().isEmpty)
    }

    func testRemoveRejectsBlankPeerId() async {
        let reg = InMemoryMeshCapabilityRegistry()
        do {
            _ = try await reg.remove(peerId: "")
            XCTFail("expected invalidPeerId")
        } catch let e as MeshCapabilityError {
            XCTAssertEqual(e, .invalidPeerId)
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    // ── Stale filtering (fixed clock) ─────────────────────────────────────────

    func testListStaleFilterUsesInjectedClock() async throws {
        let fixedNow = Date(timeIntervalSince1970: 10_000)
        let reg = InMemoryMeshCapabilityRegistry(nowUtc: { fixedNow })
        // Fresh: 30s ago. Stale: 120s ago.
        try await reg.upsert(ad("fresh", at: fixedNow.addingTimeInterval(-30)))
        try await reg.upsert(ad("stale", at: fixedNow.addingTimeInterval(-120)))

        let recent = reg.list(staleAfter: 60) // keep entries newer than 60s
        XCTAssertEqual(recent.count, 1)
        XCTAssertEqual(recent.first?.peerId, "fresh")

        // nil staleAfter returns everything.
        XCTAssertEqual(reg.list(staleAfter: nil).count, 2)
    }

    // ── Find ──────────────────────────────────────────────────────────────────

    func testFindMatchesModelCaseInsensitiveAndSortsBySpareBudgetDesc() async throws {
        let now = Date()
        let reg = InMemoryMeshCapabilityRegistry(nowUtc: { now })
        try await reg.upsert(ad("low",  model: "Qwen3-1.7B-MNN", kv: 512,  at: now))
        try await reg.upsert(ad("high", model: "qwen3-1.7b-mnn", kv: 8192, at: now)) // different case
        try await reg.upsert(ad("mid",  model: "Qwen3-1.7B-MNN", kv: 2048, at: now))
        try await reg.upsert(ad("other", model: "Llama-3B", kv: 9999, at: now))       // different model

        let hits = reg.find(modelId: "Qwen3-1.7B-MNN")
        XCTAssertEqual(hits.map { $0.peerId }, ["high", "mid", "low"]) // spare budget desc
        XCTAssertFalse(hits.contains { $0.peerId == "other" })
    }

    func testFindHonoursMinFreeKvTokens() async throws {
        let now = Date()
        let reg = InMemoryMeshCapabilityRegistry(nowUtc: { now })
        try await reg.upsert(ad("a", kv: 100, at: now))
        try await reg.upsert(ad("b", kv: 3000, at: now))
        let hits = reg.find(modelId: "Qwen3-1.7B-MNN", minFreeKvTokens: 1000)
        XCTAssertEqual(hits.map { $0.peerId }, ["b"])
    }

    func testFindHonoursStaleFilter() async throws {
        let now = Date(timeIntervalSince1970: 50_000)
        let reg = InMemoryMeshCapabilityRegistry(nowUtc: { now })
        try await reg.upsert(ad("fresh", kv: 100, at: now.addingTimeInterval(-10)))
        try await reg.upsert(ad("stale", kv: 9999, at: now.addingTimeInterval(-600)))
        let hits = reg.find(modelId: "Qwen3-1.7B-MNN", minFreeKvTokens: 0, staleAfter: 60)
        XCTAssertEqual(hits.map { $0.peerId }, ["fresh"])
    }

    func testFindBlankModelReturnsEmpty() async throws {
        let reg = InMemoryMeshCapabilityRegistry()
        try await reg.upsert(ad("a", at: Date()))
        XCTAssertTrue(reg.find(modelId: "  ").isEmpty)
    }

    // ── Codable round-trip ────────────────────────────────────────────────────

    func testAdvertisementCodableRoundTrip() throws {
        let a = ad("p", kv: 1234, tier: .workstation, ctx: 131_072,
                   at: Date(timeIntervalSince1970: 1), latency: 42)
        let back = try JSONDecoder().decode(
            MeshCapabilityAdvertisement.self, from: JSONEncoder().encode(a))
        XCTAssertEqual(back, a)
    }

    // ── Broadcasters ──────────────────────────────────────────────────────────

    func testNullBroadcasterIsNoOp() async throws {
        // Must not throw and must have no observable effect.
        try await NullMeshCapabilityBroadcaster.shared.broadcast(ad("p", at: Date()))
    }

    func testLoopbackBroadcasterUpsertsIntoRegistry() async throws {
        let now = Date()
        let reg = InMemoryMeshCapabilityRegistry(nowUtc: { now })
        let bc = LoopbackMeshCapabilityBroadcaster(registry: reg)
        try await bc.broadcast(ad("self", model: "M", kv: 2048, at: now))
        // Broadcasting our advert makes it discoverable via find.
        let hits = reg.find(modelId: "M")
        XCTAssertEqual(hits.map { $0.peerId }, ["self"])
    }
}
