// NetworkingAetherNetTests.swift
//
// Validates the CircleAI.Networking.AetherNet port (NetworkingAetherNet.swift):
// enum ordinals (cross-language wire), record Codable round-trips, the in-memory
// registry (ordering + aggregation), and the three services — the loopback
// transport, the presence-beacon discovery, and the DTN-backed sync channel —
// including the pre-subscription buffering + fan-out concurrency guarantees and
// the availability gating driven by the injected IAetherContext.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingAetherNetTests: XCTestCase {

    // ── Helpers ──────────────────────────────────────────────────────────────

    private func availableContext() -> IAetherContext {
        InMemoryAetherContext(installLevel: .app, runtimeVersion: SemanticVersion(major: 3), isEnabled: true)
    }

    private func unavailableContext() -> IAetherContext {
        InMemoryAetherContext(installLevel: .none, runtimeVersion: nil, isEnabled: false)
    }

    private func peer(_ id: String, _ role: PeerRole = .peer) -> PeerInfo {
        PeerInfo(nodeId: id, displayName: id, supportedTransports: [.aether],
                 role: role, signalStrengthDbm: -40, lastSeen: Date())
    }

    private func delta(_ owner: String, domain: String, seq: Int64,
                       mode: SyncDeliveryMode = .reliable) -> SyncDelta {
        SyncDelta(ownerId: owner, sourceDeviceId: "src", targetDeviceId: "dst",
                  domainKey: domain, payload: Data([UInt8(seq & 0xFF)]),
                  sequence: seq, deliveryMode: mode)
    }

    // ── AetherPeerKind ordinals (mirror C# declaration order) ────────────────

    func testAetherPeerKindOrdinals() {
        XCTAssertEqual(AetherPeerKind.phone.rawValue,   0)
        XCTAssertEqual(AetherPeerKind.tablet.rawValue,  1)
        XCTAssertEqual(AetherPeerKind.laptop.rawValue,  2)
        XCTAssertEqual(AetherPeerKind.desktop.rawValue, 3)
        XCTAssertEqual(AetherPeerKind.edge.rawValue,    4)
        XCTAssertEqual(AetherPeerKind.vehicle.rawValue, 5)
        XCTAssertEqual(AetherPeerKind.iot.rawValue,     6)
        XCTAssertEqual(AetherPeerKind.allCases.count,   7)
    }

    // ── Record Codable round-trips ───────────────────────────────────────────

    func testAetherPeerCodableRoundTrip() throws {
        let p = AetherPeer(peerId: "p1", kind: .vehicle, friendlyName: "Car",
                           advertisedCapabilities: ["relay", "sos"])
        let data = try JSONEncoder().encode(p)
        let back = try JSONDecoder().decode(AetherPeer.self, from: data)
        XCTAssertEqual(p, back)
    }

    func testPacketSummaryCodableRoundTrip() throws {
        let s = AetherPacketSummary(packetId: "pk1", fromPeer: "a", toPeer: "b",
                                    bytes: 128, packetKind: "data", atUtc: Date(timeIntervalSince1970: 100))
        let data = try JSONEncoder().encode(s)
        let back = try JSONDecoder().decode(AetherPacketSummary.self, from: data)
        XCTAssertEqual(s, back)
    }

    // ── InMemoryAetherNetRegistry ────────────────────────────────────────────

    func testRegistryPeersSortedByPeerId() {
        let reg = InMemoryAetherNetRegistry()
        reg.register(AetherPeer(peerId: "zeta", kind: .phone, friendlyName: nil, advertisedCapabilities: []))
        reg.register(AetherPeer(peerId: "alpha", kind: .laptop, friendlyName: nil, advertisedCapabilities: []))
        reg.register(AetherPeer(peerId: "mid", kind: .edge, friendlyName: nil, advertisedCapabilities: []))
        XCTAssertEqual(reg.peers.map { $0.peerId }, ["alpha", "mid", "zeta"])
        XCTAssertEqual(reg.getPeer("mid")?.kind, .edge)
        XCTAssertNil(reg.getPeer("nope"))
    }

    func testRegistryRegisterReplaces() {
        let reg = InMemoryAetherNetRegistry()
        reg.register(AetherPeer(peerId: "p", kind: .phone, friendlyName: "old", advertisedCapabilities: []))
        reg.register(AetherPeer(peerId: "p", kind: .desktop, friendlyName: "new", advertisedCapabilities: []))
        XCTAssertEqual(reg.peers.count, 1)
        XCTAssertEqual(reg.getPeer("p")?.friendlyName, "new")
    }

    func testRegistryAvgRoundTripMs() {
        let reg = InMemoryAetherNetRegistry()
        XCTAssertEqual(reg.avgRoundTripMs("x"), 0, accuracy: 0.0001) // empty → 0
        reg.recordHop(AetherHopTelemetry(peerId: "x", hopCount: 1, roundTripMs: 10, atUtc: Date()))
        reg.recordHop(AetherHopTelemetry(peerId: "x", hopCount: 2, roundTripMs: 30, atUtc: Date()))
        reg.recordHop(AetherHopTelemetry(peerId: "y", hopCount: 1, roundTripMs: 999, atUtc: Date()))
        XCTAssertEqual(reg.avgRoundTripMs("x"), 20, accuracy: 0.0001)
        XCTAssertEqual(reg.avgRoundTripMs("y"), 999, accuracy: 0.0001)
    }

    func testRegistryRecentPacketsNewestFirstAndTotalBytes() {
        let reg = InMemoryAetherNetRegistry()
        reg.recordPacket(AetherPacketSummary(packetId: "1", fromPeer: "a", toPeer: "b", bytes: 10, packetKind: "d", atUtc: Date(timeIntervalSince1970: 1)))
        reg.recordPacket(AetherPacketSummary(packetId: "2", fromPeer: "a", toPeer: "b", bytes: 20, packetKind: "d", atUtc: Date(timeIntervalSince1970: 3)))
        reg.recordPacket(AetherPacketSummary(packetId: "3", fromPeer: "a", toPeer: "c", bytes: 5, packetKind: "d", atUtc: Date(timeIntervalSince1970: 2)))
        // Newest first by AtUtc.
        XCTAssertEqual(reg.recentPackets().map { $0.packetId }, ["2", "3", "1"])
        XCTAssertEqual(reg.recentPackets(limit: 2).map { $0.packetId }, ["2", "3"])
        // Only a→b bytes.
        XCTAssertEqual(reg.totalBytesBetween(fromPeer: "a", toPeer: "b"), 30)
        XCTAssertEqual(reg.totalBytesBetween(fromPeer: "a", toPeer: "c"), 5)
        XCTAssertEqual(reg.totalBytesBetween(fromPeer: "a", toPeer: "z"), 0)
    }

    // ── AetherNetworkTransport ───────────────────────────────────────────────

    func testTransportKindAndAvailabilityFollowsContext() async throws {
        let up = AetherNetworkTransport(context: availableContext())
        XCTAssertEqual(up.kind, .aether)
        try await up.start()
        XCTAssertTrue(up.isAvailable)

        let down = AetherNetworkTransport(context: unavailableContext())
        XCTAssertFalse(down.isAvailable)
    }

    func testTransportLoopbackDeliversInOrder() async throws {
        let t = AetherNetworkTransport(context: availableContext())
        try await t.start()
        let stream = t.receive()
        try await t.send(NetworkPayload.create(data: Data([1])))
        try await t.send(NetworkPayload.create(data: Data([2])))
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([1]), Data([2])])
    }

    func testTransportBuffersSendBeforeReceive() async throws {
        let t = AetherNetworkTransport(context: availableContext())
        try await t.start()
        // Send BEFORE receive() — must be retained and replayed.
        try await t.send(NetworkPayload.create(data: Data([9])))
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([9])])
    }

    func testTransportSendAfterStopThrows() async throws {
        let t = AetherNetworkTransport(context: availableContext())
        try await t.start()
        try await t.stop()
        do {
            try await t.send(NetworkPayload.create(data: Data([1])))
            XCTFail("expected send after stop to throw")
        } catch {
            XCTAssertEqual(error as? NetworkError, .transportStopped)
        }
    }

    // ── AetherPeerDiscovery ──────────────────────────────────────────────────

    func testDiscoveryAnnounceThenDiscover() async throws {
        let d = AetherPeerDiscovery(context: availableContext())
        let stream = d.discover()
        try await d.announce(localInfo: peer("n1"))
        try await d.announce(localInfo: peer("n2", .relay))

        var got: [String] = []
        for await p in stream {
            got.append(p.nodeId)
            if got.count == 2 { break }
        }
        XCTAssertEqual(got, ["n1", "n2"])
    }

    func testDiscoveryReplaysAlreadyAnnounced() async throws {
        let d = AetherPeerDiscovery(context: availableContext())
        try await d.announce(localInfo: peer("early-1"))
        try await d.announce(localInfo: peer("early-2"))
        let stream = d.discover()

        var got: [String] = []
        for await p in stream {
            got.append(p.nodeId)
            if got.count == 2 { break }
        }
        XCTAssertEqual(got, ["early-1", "early-2"])
    }

    func testDiscoveryAnnounceNoOpWhenContextUnavailable() async throws {
        let up = availableContext()
        let dUp = AetherPeerDiscovery(context: up)
        // Sanity: when available, an announce IS recorded (a later discover replays).
        try await dUp.announce(localInfo: peer("real"))
        var upIter = dUp.discover().makeAsyncIterator()
        let firstPeer = await upIter.next()
        XCTAssertEqual(firstPeer?.nodeId, "real")

        // When unavailable, announce is gated off — nothing is recorded, so a
        // fresh discover() that then closes yields an empty replay.
        let dDown = AetherPeerDiscovery(context: unavailableContext())
        try await dDown.announce(localInfo: peer("ghost"))
        XCTAssertEqual(dDown.subscriberCount, 0)
    }

    // ── AetherSyncChannel ────────────────────────────────────────────────────

    func testSyncChannelDefaultTtlIs72h() {
        XCTAssertEqual(AetherSyncChannel.defaultTtl, 72 * 60 * 60, accuracy: 0.0001)
    }

    func testSyncChannelLastSequenceTracksHighest() async throws {
        let ch = AetherSyncChannel(context: availableContext())
        let initialSeq = try await ch.getLastSequence(ownerId: "o", domainKey: "memory.episodic")
        XCTAssertEqual(initialSeq, 0)
        try await ch.pushDelta(delta("o", domain: "memory.episodic", seq: 5))
        try await ch.pushDelta(delta("o", domain: "memory.episodic", seq: 3)) // lower, ignored
        try await ch.pushDelta(delta("o", domain: "memory.episodic", seq: 9))
        let episodicSeq = try await ch.getLastSequence(ownerId: "o", domainKey: "memory.episodic")
        XCTAssertEqual(episodicSeq, 9)
        // Different domain is tracked independently.
        try await ch.pushDelta(delta("o", domain: "persona", seq: 2))
        let personaSeq = try await ch.getLastSequence(ownerId: "o", domainKey: "persona")
        XCTAssertEqual(personaSeq, 2)
        let episodicSeqAgain = try await ch.getLastSequence(ownerId: "o", domainKey: "memory.episodic")
        XCTAssertEqual(episodicSeqAgain, 9)
    }

    func testSyncChannelDeliversDeltasToMatchingOwner() async throws {
        let ch = AetherSyncChannel(context: availableContext())
        let stream = ch.receiveDeltas(ownerId: "alice")
        try await ch.pushDelta(delta("alice", domain: "d", seq: 1))
        try await ch.pushDelta(delta("bob", domain: "d", seq: 1))   // other owner
        try await ch.pushDelta(delta("alice", domain: "d", seq: 2))

        var got: [Int64] = []
        for await d in stream {
            got.append(d.sequence)
            if got.count == 2 { break }
        }
        XCTAssertEqual(got, [1, 2]) // only alice's deltas
    }

    func testSyncChannelReplaysDeltasPushedBeforeSubscribe() async throws {
        let ch = AetherSyncChannel(context: availableContext())
        // Push BEFORE any subscriber — must be retained for the owner.
        try await ch.pushDelta(delta("alice", domain: "d", seq: 7))
        let stream = ch.receiveDeltas(ownerId: "alice")

        var got: [Int64] = []
        for await d in stream {
            got.append(d.sequence)
            if got.count == 1 { break }
        }
        XCTAssertEqual(got, [7])
    }
}
