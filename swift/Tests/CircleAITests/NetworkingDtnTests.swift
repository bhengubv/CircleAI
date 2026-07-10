// NetworkingDtnTests.swift
//
// Validates the CircleAI.Networking.Dtn port (NetworkingDtn.swift): DtnBundle /
// DtnCustodyRecord Codable, DtnPriority ordinals, the InMemoryDtnBundleStore
// (store / custody / isExpired-unknown-is-expired / purge / inFlightTo), and the
// DtnSyncChannel — the first-available-transport send path, the delivery-mode →
// custody/priority mapping (via the formed bundle + captured payload), sequence
// tracking, and the delta loopback delivery with pre-subscription buffering.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingDtnTests: XCTestCase {

    // ── A capturing transport (records the payload it is asked to send) ───────
    private final class CapturingTransport: INetworkTransport, @unchecked Sendable {
        private let lock = NSLock()
        private var _available: Bool
        private var _sent: [NetworkPayload] = []

        init(available: Bool) { self._available = available }

        var kind: TransportKind { .dtn }
        var isAvailable: Bool { lock.lock(); defer { lock.unlock() }; return _available }
        func start() async throws {}
        func stop() async throws {}
        func send(_ payload: NetworkPayload) async throws {
            lock.lock(); _sent.append(payload); lock.unlock()
        }
        func receive() -> AsyncStream<NetworkPayload> { AsyncStream { $0.finish() } }

        var sent: [NetworkPayload] { lock.lock(); defer { lock.unlock() }; return _sent }
    }

    private func delta(_ owner: String, domain: String = "d", seq: Int64,
                       mode: SyncDeliveryMode, target: String = "dst",
                       payload: Data = Data([1])) -> SyncDelta {
        SyncDelta(ownerId: owner, sourceDeviceId: "src", targetDeviceId: target,
                  domainKey: domain, payload: payload, sequence: seq, deliveryMode: mode)
    }

    private func bundle(_ id: String, dest: String, expiresAt: Date) -> DtnBundle {
        DtnBundle(bundleId: id, sourceNodeId: "s", destinationNodeId: dest,
                  payload: Data([1]), expiresAt: expiresAt, custodyRequired: false,
                  hopCount: 0, createdAt: Date(timeIntervalSince1970: 0))
    }

    // ── DtnPriority ordinals ─────────────────────────────────────────────────

    func testDtnPriorityOrdinalsAndComparable() {
        XCTAssertEqual(DtnPriority.bulk.rawValue,      0)
        XCTAssertEqual(DtnPriority.normal.rawValue,    1)
        XCTAssertEqual(DtnPriority.expedited.rawValue, 2)
        XCTAssertTrue(DtnPriority.bulk < DtnPriority.expedited)
        XCTAssertEqual([DtnPriority.bulk, .expedited, .normal].max(), .expedited)
        XCTAssertEqual(DtnPriority.allCases.count, 3)
    }

    func testBundleCodableRoundTrip() throws {
        let b = bundle("b1", dest: "d", expiresAt: Date(timeIntervalSince1970: 1000))
        let data = try JSONEncoder().encode(b)
        let back = try JSONDecoder().decode(DtnBundle.self, from: data)
        XCTAssertEqual(b, back)
    }

    // ── InMemoryDtnBundleStore ───────────────────────────────────────────────

    func testStoreAndGet() {
        let store = InMemoryDtnBundleStore()
        XCTAssertNil(store.get("nope"))
        let b = bundle("b1", dest: "d", expiresAt: Date(timeIntervalSince1970: 1000))
        store.store(b)
        XCTAssertEqual(store.get("b1"), b)
        XCTAssertEqual(store.all.count, 1)
    }

    func testCustody() {
        let store = InMemoryDtnBundleStore()
        XCTAssertNil(store.getCustody("b1"))
        let rec = DtnCustodyRecord(bundleId: "b1", custodianNode: "node-x", acceptedAtUtc: Date(timeIntervalSince1970: 5))
        store.acceptCustody(rec)
        XCTAssertEqual(store.getCustody("b1"), rec)
    }

    func testIsExpiredUnknownBundleIsExpired() {
        let store = InMemoryDtnBundleStore()
        // Unknown bundle → expired (mirrors C#).
        XCTAssertTrue(store.isExpired("ghost", now: Date(timeIntervalSince1970: 0)))

        store.store(bundle("b", dest: "d", expiresAt: Date(timeIntervalSince1970: 100)))
        XCTAssertFalse(store.isExpired("b", now: Date(timeIntervalSince1970: 50)))
        XCTAssertTrue(store.isExpired("b", now: Date(timeIntervalSince1970: 150)))
        // Exactly at expiry is NOT expired (C# uses strict `now > ExpiresAt`).
        XCTAssertFalse(store.isExpired("b", now: Date(timeIntervalSince1970: 100)))
    }

    func testPurgeRemovesExpiredAndCustody() {
        let store = InMemoryDtnBundleStore()
        store.store(bundle("live", dest: "d", expiresAt: Date(timeIntervalSince1970: 1000)))
        store.store(bundle("dead", dest: "d", expiresAt: Date(timeIntervalSince1970: 10)))
        store.acceptCustody(DtnCustodyRecord(bundleId: "dead", custodianNode: "n", acceptedAtUtc: Date(timeIntervalSince1970: 0)))

        let removed = store.purge(now: Date(timeIntervalSince1970: 100))
        XCTAssertEqual(removed, 1)
        XCTAssertNil(store.get("dead"))
        XCTAssertNil(store.getCustody("dead"))
        XCTAssertNotNil(store.get("live"))
    }

    func testInFlightTo() {
        let store = InMemoryDtnBundleStore()
        store.store(bundle("1", dest: "alice", expiresAt: Date(timeIntervalSince1970: 1000)))
        store.store(bundle("2", dest: "bob", expiresAt: Date(timeIntervalSince1970: 1000)))
        store.store(bundle("3", dest: "alice", expiresAt: Date(timeIntervalSince1970: 1000)))
        XCTAssertEqual(Set(store.inFlightTo("alice").map { $0.bundleId }), ["1", "3"])
        XCTAssertEqual(store.inFlightTo("bob").map { $0.bundleId }, ["2"])
        XCTAssertTrue(store.inFlightTo("carol").isEmpty)
    }

    // ── DtnSyncChannel: send path ────────────────────────────────────────────

    func testChannelDefaultTtlAndModeMapping() {
        XCTAssertEqual(DtnSyncChannel.defaultTtl, 72 * 60 * 60, accuracy: 0.0001)
        XCTAssertEqual(DtnSyncChannel.guaranteedMode, .reliable)
        XCTAssertEqual(DtnSyncChannel.urgentMode, .realtime)
    }

    func testChannelSendsViaFirstAvailableTransport() async throws {
        let down = CapturingTransport(available: false)
        let up = CapturingTransport(available: true)
        let ch = DtnSyncChannel(transports: [down, up])
        try await ch.pushDelta(delta("o", seq: 1, mode: .reliable, target: "dest-id"))

        // The unavailable transport is skipped; the first available carries it.
        XCTAssertTrue(down.sent.isEmpty)
        XCTAssertEqual(up.sent.count, 1)
        let p = up.sent[0]
        XCTAssertEqual(p.destinationId, "dest-id")
        XCTAssertEqual(p.contentType, "application/dtn-bundle")
    }

    func testChannelUrgentModeMapsToUrgentPriority() async throws {
        let up = CapturingTransport(available: true)
        let ch = DtnSyncChannel(transports: [up])
        // urgentMode (.realtime) → MessagePriority.urgent.
        try await ch.pushDelta(delta("o", seq: 1, mode: DtnSyncChannel.urgentMode))
        XCTAssertEqual(up.sent.first?.priority, .urgent)

        // Any non-urgent mode → MessagePriority.normal.
        let up2 = CapturingTransport(available: true)
        let ch2 = DtnSyncChannel(transports: [up2])
        try await ch2.pushDelta(delta("o", seq: 1, mode: .reliable))
        XCTAssertEqual(up2.sent.first?.priority, .normal)
    }

    func testChannelGuaranteedModeSetsCustodyRequiredOnBundle() async throws {
        let up = CapturingTransport(available: true)
        let ch = DtnSyncChannel(transports: [up])
        try await ch.pushDelta(delta("o", seq: 1, mode: DtnSyncChannel.guaranteedMode))
        XCTAssertEqual(ch.lastBundle()?.custodyRequired, true)

        let up2 = CapturingTransport(available: true)
        let ch2 = DtnSyncChannel(transports: [up2])
        try await ch2.pushDelta(delta("o", seq: 1, mode: .realtime))
        XCTAssertEqual(ch2.lastBundle()?.custodyRequired, false)
    }

    func testChannelBundleTtlUsesDeltaTtlThenDefault() async throws {
        let up = CapturingTransport(available: true)
        let ch = DtnSyncChannel(transports: [up])

        // Explicit TTL on the delta wins.
        var d = delta("o", seq: 1, mode: .reliable)
        d.ttl = 60
        try await ch.pushDelta(d)
        let b = try XCTUnwrap(ch.lastBundle())
        XCTAssertEqual(b.expiresAt.timeIntervalSince(b.createdAt), 60, accuracy: 1.0)

        // No TTL → 72h default.
        try await ch.pushDelta(delta("o", seq: 2, mode: .reliable))
        let b2 = try XCTUnwrap(ch.lastBundle())
        XCTAssertEqual(b2.expiresAt.timeIntervalSince(b2.createdAt),
                       DtnSyncChannel.defaultTtl, accuracy: 1.0)
    }

    func testChannelQueuesWhenNoTransportAvailable() async throws {
        let down = CapturingTransport(available: false)
        let ch = DtnSyncChannel(transports: [down])
        // No available transport — nothing is sent, but sequence + loopback still work.
        try await ch.pushDelta(delta("o", domain: "dk", seq: 4, mode: .reliable))
        XCTAssertTrue(down.sent.isEmpty)
        XCTAssertEqual(try await ch.getLastSequence(ownerId: "o", domainKey: "dk"), 4)
    }

    // ── DtnSyncChannel: sequence + delivery ──────────────────────────────────

    func testChannelLastSequenceTracksHighest() async throws {
        let ch = DtnSyncChannel(transports: [])
        try await ch.pushDelta(delta("o", domain: "dk", seq: 2, mode: .reliable))
        try await ch.pushDelta(delta("o", domain: "dk", seq: 8, mode: .reliable))
        try await ch.pushDelta(delta("o", domain: "dk", seq: 5, mode: .reliable)) // lower
        XCTAssertEqual(try await ch.getLastSequence(ownerId: "o", domainKey: "dk"), 8)
    }

    func testChannelDeliversDeltasToMatchingOwner() async throws {
        let ch = DtnSyncChannel(transports: [])
        let stream = ch.receiveDeltas(ownerId: "alice")
        try await ch.pushDelta(delta("alice", seq: 1, mode: .reliable))
        try await ch.pushDelta(delta("bob", seq: 1, mode: .reliable))    // other owner
        try await ch.pushDelta(delta("alice", seq: 2, mode: .reliable))

        var got: [Int64] = []
        for await d in stream {
            got.append(d.sequence)
            if got.count == 2 { break }
        }
        XCTAssertEqual(got, [1, 2])
    }

    func testChannelReplaysDeltasPushedBeforeSubscribe() async throws {
        let ch = DtnSyncChannel(transports: [])
        try await ch.pushDelta(delta("alice", seq: 7, mode: .reliable))
        let stream = ch.receiveDeltas(ownerId: "alice")
        var got: [Int64] = []
        for await d in stream {
            got.append(d.sequence)
            if got.count == 1 { break }
        }
        XCTAssertEqual(got, [7])
    }
}
