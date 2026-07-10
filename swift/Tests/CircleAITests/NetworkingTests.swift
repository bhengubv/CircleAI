// NetworkingTests.swift
//
// Validates the CircleAI.Networking port (Networking.swift): enum ordinals
// (cross-language wire), DTO factories/derived values, Codable round-trips, the
// default transport selector cascade + policy interaction, the policy builder,
// and the working in-memory implementations of every contract (transport,
// message channel, connectivity monitor, payload optimiser, peer discovery,
// mesh network) — including the pre-subscription buffering and fan-out
// concurrency guarantees.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingTests: XCTestCase {

    // ── Enum ordinals (mirror C# declaration order — cross-language wire) ─────

    func testTransportKindOrdinals() {
        XCTAssertEqual(TransportKind.http.rawValue,       0)
        XCTAssertEqual(TransportKind.webSocket.rawValue,  1)
        XCTAssertEqual(TransportKind.grpc.rawValue,       2)
        XCTAssertEqual(TransportKind.mqtt.rawValue,       3)
        XCTAssertEqual(TransportKind.tcp.rawValue,        4)
        XCTAssertEqual(TransportKind.udp.rawValue,        5)
        XCTAssertEqual(TransportKind.wiFi.rawValue,       6)
        XCTAssertEqual(TransportKind.bluetooth.rawValue,  7)
        XCTAssertEqual(TransportKind.nearLink.rawValue,   8)
        XCTAssertEqual(TransportKind.aether.rawValue,     9)
        XCTAssertEqual(TransportKind.dtn.rawValue,        10)
        XCTAssertEqual(TransportKind.localStore.rawValue, 11)
        XCTAssertEqual(TransportKind.allCases.count, 12)
    }

    func testConnectivityStateOrdinals() {
        XCTAssertEqual(ConnectivityState.online.rawValue,    0)
        XCTAssertEqual(ConnectivityState.localOnly.rawValue, 1)
        XCTAssertEqual(ConnectivityState.meshOnly.rawValue,  2)
        XCTAssertEqual(ConnectivityState.offline.rawValue,   3)
    }

    func testMessagePriorityOrdinalsAndComparable() {
        XCTAssertEqual(MessagePriority.low.rawValue,       0)
        XCTAssertEqual(MessagePriority.normal.rawValue,    1)
        XCTAssertEqual(MessagePriority.high.rawValue,      2)
        XCTAssertEqual(MessagePriority.urgent.rawValue,    3)
        XCTAssertEqual(MessagePriority.emergency.rawValue, 4)
        XCTAssertTrue(MessagePriority.low < MessagePriority.emergency)
        XCTAssertEqual([MessagePriority.low, .emergency, .high].max(), .emergency)
    }

    func testPeerRoleOrdinals() {
        XCTAssertEqual(PeerRole.peer.rawValue,   0)
        XCTAssertEqual(PeerRole.relay.rawValue,  1)
        XCTAssertEqual(PeerRole.bridge.rawValue, 2)
        XCTAssertEqual(PeerRole.sink.rawValue,   3)
    }

    // ── NetworkPayload ────────────────────────────────────────────────────────

    func testNetworkPayloadCreateDefaults() {
        let p = NetworkPayload.create(data: Data([1, 2, 3]))
        XCTAssertNil(p.sourceId)
        XCTAssertNil(p.destinationId)
        XCTAssertEqual(p.priority, .normal)
        XCTAssertEqual(p.contentType, "application/octet-stream")
        XCTAssertNil(p.ttl)
        XCTAssertTrue(p.metadata.isEmpty)
        XCTAssertEqual(p.data, Data([1, 2, 3]))
        // 32-char lowercase hex, no dashes (Guid "N" format).
        XCTAssertEqual(p.id.count, 32)
        XCTAssertFalse(p.id.contains("-"))
        XCTAssertEqual(p.id, p.id.lowercased())
    }

    func testNetworkPayloadCreateUniqueIds() {
        let a = NetworkPayload.create(data: Data())
        let b = NetworkPayload.create(data: Data())
        XCTAssertNotEqual(a.id, b.id)
    }

    func testNetworkPayloadCreateWithArgs() {
        let p = NetworkPayload.create(
            data: Data([9]),
            destinationId: "dest",
            priority: .urgent,
            contentType: "application/json",
            ttl: 30)
        XCTAssertEqual(p.destinationId, "dest")
        XCTAssertEqual(p.priority, .urgent)
        XCTAssertEqual(p.contentType, "application/json")
        XCTAssertEqual(p.ttl, 30)
    }

    func testNetworkPayloadCodableRoundTrip() throws {
        let original = NetworkPayload(
            id: "abc",
            sourceId: "src",
            destinationId: "dst",
            data: Data([1, 2, 3, 4]),
            priority: .high,
            ttl: 12.5,
            contentType: "text/plain",
            metadata: ["k": "v"],
            createdAt: Date(timeIntervalSince1970: 1000))
        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(NetworkPayload.self, from: data)
        XCTAssertEqual(decoded, original)
    }

    // ── NetworkContext ──────────────────────────────────────────────────────

    func testNetworkContextOffline() {
        let ctx = NetworkContext.offline
        XCTAssertEqual(ctx.state, .offline)
        XCTAssertEqual(ctx.preferredTransport, .localStore)
        XCTAssertTrue(ctx.availableTransports.isEmpty)
        XCTAssertNil(ctx.signalStrengthDbm)
        XCTAssertNil(ctx.estimatedBandwidthBps)
        XCTAssertNil(ctx.latencyMs)
        XCTAssertEqual(ctx.nearbyPeerCount, 0)
    }

    func testNetworkContextCodableRoundTrip() throws {
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: [.grpc, .http, .wiFi],
            signalStrengthDbm: -55,
            estimatedBandwidthBps: 1_000_000,
            latencyMs: 25,
            nearbyPeerCount: 3,
            snapshotAt: Date(timeIntervalSince1970: 2000))
        let data = try JSONEncoder().encode(ctx)
        let decoded = try JSONDecoder().decode(NetworkContext.self, from: data)
        XCTAssertEqual(decoded, ctx)
    }

    // ── SchedulingHint / PeerInfo Codable ─────────────────────────────────────

    func testSchedulingHintCodableRoundTrip() throws {
        let hint = SchedulingHint(
            preferredPeerIds: ["a", "b"],
            suggestedWindowUtc: Date(timeIntervalSince1970: 3000),
            confidenceScore: 0.85)
        let data = try JSONEncoder().encode(hint)
        let decoded = try JSONDecoder().decode(SchedulingHint.self, from: data)
        XCTAssertEqual(decoded, hint)
    }

    func testPeerInfoCodableRoundTrip() throws {
        let peer = PeerInfo(
            nodeId: "node-1",
            displayName: "Phone",
            supportedTransports: [.bluetooth, .wiFi],
            role: .relay,
            signalStrengthDbm: -40,
            lastSeen: Date(timeIntervalSince1970: 4000))
        let data = try JSONEncoder().encode(peer)
        let decoded = try JSONDecoder().decode(PeerInfo.self, from: data)
        XCTAssertEqual(decoded, peer)
    }

    // ── DefaultNetworkPolicy ─────────────────────────────────────────────────

    func testDefaultNetworkPolicyPermissive() {
        let policy = DefaultNetworkPolicy.shared
        let p = NetworkPayload.create(data: Data())
        for kind in TransportKind.allCases {
            XCTAssertTrue(policy.permits(kind, payload: p))
        }
        XCTAssertNil(policy.forceTransport)
        XCTAssertFalse(policy.meshFirst)
        XCTAssertTrue(policy.offlineQueueEnabled)
        XCTAssertTrue(policy.allowCloudTransports)
        // Singleton identity.
        XCTAssertTrue(DefaultNetworkPolicy.shared === DefaultNetworkPolicy.shared)
    }

    // ── NetworkPolicyBuilder ─────────────────────────────────────────────────

    func testPolicyBuilderEmptyAllowsAll() {
        let policy = NetworkPolicyBuilder().build()
        let p = NetworkPayload.create(data: Data())
        for kind in TransportKind.allCases {
            XCTAssertTrue(policy.permits(kind, payload: p))
        }
        XCTAssertTrue(policy.allowCloudTransports)
        XCTAssertFalse(policy.meshFirst)
        XCTAssertTrue(policy.offlineQueueEnabled)
        XCTAssertNil(policy.forceTransport)
    }

    func testPolicyBuilderAllowRestrictsSet() {
        let policy = NetworkPolicyBuilder().allow(.wiFi, .bluetooth).build()
        let p = NetworkPayload.create(data: Data())
        XCTAssertTrue(policy.permits(.wiFi, payload: p))
        XCTAssertTrue(policy.permits(.bluetooth, payload: p))
        XCTAssertFalse(policy.permits(.http, payload: p))
        XCTAssertFalse(policy.permits(.aether, payload: p))
    }

    func testPolicyBuilderNoCloudBlocksCloudTransports() {
        let policy = NetworkPolicyBuilder().noCloud().build()
        let p = NetworkPayload.create(data: Data())
        // Cloud transports blocked.
        XCTAssertFalse(policy.permits(.http, payload: p))
        XCTAssertFalse(policy.permits(.webSocket, payload: p))
        XCTAssertFalse(policy.permits(.grpc, payload: p))
        XCTAssertFalse(policy.permits(.mqtt, payload: p))
        // Non-cloud transports still allowed.
        XCTAssertTrue(policy.permits(.wiFi, payload: p))
        XCTAssertTrue(policy.permits(.aether, payload: p))
        XCTAssertTrue(policy.permits(.tcp, payload: p))
        XCTAssertFalse(policy.allowCloudTransports)
    }

    func testPolicyBuilderFlagsAndForce() {
        let policy = NetworkPolicyBuilder()
            .meshFirst()
            .disableQueue()
            .force(.aether)
            .build()
        XCTAssertTrue(policy.meshFirst)
        XCTAssertFalse(policy.offlineQueueEnabled)
        XCTAssertEqual(policy.forceTransport, .aether)
    }

    // ── DefaultTransportSelector ─────────────────────────────────────────────

    func testSelectorFullCascadeWhenAllAvailable() {
        let selector = DefaultTransportSelector()
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: TransportKind.allCases,
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 0,
            snapshotAt: Date())
        let p = NetworkPayload.create(data: Data())
        // gRPC leads the documented cascade.
        XCTAssertEqual(selector.selectBest(p, context: ctx), .grpc)
        let cascade = selector.getCascade(p, context: ctx)
        XCTAssertEqual(cascade.first, .grpc)
        XCTAssertEqual(cascade.last, .localStore)
        // Documented order for the leading cloud transports.
        XCTAssertEqual(Array(cascade.prefix(5)), [.grpc, .webSocket, .http, .mqtt, .tcp])
    }

    func testSelectorFiltersUnavailable() {
        let selector = DefaultTransportSelector()
        // Only WiFi + LocalStore available.
        let ctx = NetworkContext(
            state: .localOnly,
            preferredTransport: .wiFi,
            availableTransports: [.wiFi],
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 1,
            snapshotAt: Date())
        let p = NetworkPayload.create(data: Data())
        XCTAssertEqual(selector.selectBest(p, context: ctx), .wiFi)
        XCTAssertEqual(selector.getCascade(p, context: ctx), [.wiFi, .localStore])
    }

    func testSelectorForceTransportOverride() {
        let policy = NetworkPolicyBuilder().force(.bluetooth).build()
        let selector = DefaultTransportSelector(policy: policy)
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: TransportKind.allCases,
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 0,
            snapshotAt: Date())
        let p = NetworkPayload.create(data: Data())
        XCTAssertEqual(selector.selectBest(p, context: ctx), .bluetooth)
        XCTAssertEqual(selector.getCascade(p, context: ctx), [.bluetooth])
    }

    func testSelectorMeshFirstBias() {
        let policy = NetworkPolicyBuilder().meshFirst().build()
        let selector = DefaultTransportSelector(policy: policy)
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: TransportKind.allCases,
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 2,
            snapshotAt: Date())
        let p = NetworkPayload.create(data: Data())
        // Mesh transports pulled to the front, in cascade sub-order.
        let cascade = selector.getCascade(p, context: ctx)
        XCTAssertEqual(Array(cascade.prefix(4)), [.wiFi, .bluetooth, .nearLink, .aether])
        XCTAssertEqual(selector.selectBest(p, context: ctx), .wiFi)
    }

    func testSelectorEmergencyPayloadIsMeshFirst() {
        let selector = DefaultTransportSelector() // default (non-mesh-first) policy
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: TransportKind.allCases,
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 2,
            snapshotAt: Date())
        let emergency = NetworkPayload.create(data: Data(), priority: .emergency)
        // Emergency biases to mesh even under the default policy.
        XCTAssertEqual(selector.selectBest(emergency, context: ctx), .wiFi)
    }

    func testSelectorLocalStoreBackstopWhenNothingAvailable() {
        let selector = DefaultTransportSelector() // offline queue enabled
        let p = NetworkPayload.create(data: Data())
        // Fully offline context — no live transports.
        XCTAssertEqual(selector.selectBest(p, context: .offline), .localStore)
        XCTAssertEqual(selector.getCascade(p, context: .offline), [.localStore])
    }

    func testSelectorEmptyCascadeWhenQueueDisabledAndNothingAvailable() {
        let policy = NetworkPolicyBuilder().disableQueue().build()
        let selector = DefaultTransportSelector(policy: policy)
        let p = NetworkPayload.create(data: Data())
        // No transports available AND queue disabled → no route.
        XCTAssertTrue(selector.getCascade(p, context: .offline).isEmpty)
        // selectBest still returns a defined value (LocalStore fallback).
        XCTAssertEqual(selector.selectBest(p, context: .offline), .localStore)
    }

    func testSelectorNoCloudPolicyExcludesCloud() {
        let policy = NetworkPolicyBuilder().noCloud().build()
        let selector = DefaultTransportSelector(policy: policy)
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: TransportKind.allCases,
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 0,
            snapshotAt: Date())
        let p = NetworkPayload.create(data: Data())
        let cascade = selector.getCascade(p, context: ctx)
        XCTAssertFalse(cascade.contains(.grpc))
        XCTAssertFalse(cascade.contains(.webSocket))
        XCTAssertFalse(cascade.contains(.http))
        XCTAssertFalse(cascade.contains(.mqtt))
        // First surviving transport is TCP (first non-cloud in the cascade).
        XCTAssertEqual(cascade.first, .tcp)
    }

    // ── InMemoryNetworkTransport ─────────────────────────────────────────────

    func testTransportStartStopAvailability() async throws {
        let t = InMemoryNetworkTransport(kind: .tcp)
        XCTAssertEqual(t.kind, .tcp)
        XCTAssertFalse(t.isAvailable)
        try await t.start()
        XCTAssertTrue(t.isAvailable)
        try await t.stop()
        XCTAssertFalse(t.isAvailable)
    }

    func testTransportLoopbackDeliversInOrder() async throws {
        let t = InMemoryNetworkTransport(kind: .tcp)
        try await t.start()
        // Subscribe FIRST (eager build closure registers the continuation), then
        // send, then stop to terminate the stream.
        let stream = t.receive()
        let p1 = NetworkPayload.create(data: Data([1]))
        let p2 = NetworkPayload.create(data: Data([2]))
        try await t.send(p1)
        try await t.send(p2)
        try await t.stop()

        var got: [Data] = []
        for await payload in stream { got.append(payload.data) }
        XCTAssertEqual(got, [Data([1]), Data([2])])
    }

    func testTransportBuffersSendBeforeReceive() async throws {
        // A send BEFORE receive() must be retained (unbounded), not lost.
        let t = InMemoryNetworkTransport(kind: .tcp)
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([7])))
        try await t.send(NetworkPayload.create(data: Data([8])))
        // Now subscribe — pending drains into the stream buffer.
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await payload in stream { got.append(payload.data) }
        XCTAssertEqual(got, [Data([7]), Data([8])])
    }

    func testTransportSendAfterStopThrows() async throws {
        let t = InMemoryNetworkTransport(kind: .tcp)
        try await t.start()
        try await t.stop()
        do {
            try await t.send(NetworkPayload.create(data: Data([1])))
            XCTFail("expected send after stop to throw")
        } catch {
            XCTAssertEqual(error as? NetworkError, .transportStopped)
        }
    }

    // ── InMemoryMessageChannel ───────────────────────────────────────────────

    struct Ping: Codable, Sendable, Equatable {
        let seq: Int
        let text: String
    }

    struct Pong: Codable, Sendable, Equatable {
        let ok: Bool
    }

    func testMessageChannelTypedRoundTrip() async throws {
        let channel = InMemoryMessageChannel()
        // Subscribe synchronously before sending (the internal envelope stream is
        // registered eagerly inside receive()).
        let stream = channel.receive(Ping.self)
        try await channel.send(destinationId: "d", message: Ping(seq: 1, text: "hi"))
        try await channel.send(destinationId: "d", message: Ping(seq: 2, text: "yo"))
        channel.close()

        var got: [Ping] = []
        for await ping in stream { got.append(ping) }
        XCTAssertEqual(got, [Ping(seq: 1, text: "hi"), Ping(seq: 2, text: "yo")])
    }

    func testMessageChannelFiltersOtherTypes() async throws {
        let channel = InMemoryMessageChannel()
        let pingStream = channel.receive(Ping.self)
        // Send a Pong and a Ping — the Ping subscriber must see only the Ping.
        try await channel.send(destinationId: "d", message: Pong(ok: true))
        try await channel.send(destinationId: "d", message: Ping(seq: 9, text: "only"))
        channel.close()

        var got: [Ping] = []
        for await ping in pingStream { got.append(ping) }
        XCTAssertEqual(got, [Ping(seq: 9, text: "only")])
    }

    func testMessageChannelBuffersSendBeforeSubscribe() async throws {
        let channel = InMemoryMessageChannel()
        // Send BEFORE any subscriber — must be retained and replayed to the first.
        try await channel.send(destinationId: "d", message: Ping(seq: 42, text: "early"))
        let stream = channel.receive(Ping.self)
        channel.close()

        var got: [Ping] = []
        for await ping in stream { got.append(ping) }
        XCTAssertEqual(got, [Ping(seq: 42, text: "early")])
    }

    func testMessageChannelSendAfterCloseThrows() async throws {
        let channel = InMemoryMessageChannel()
        channel.close()
        do {
            try await channel.send(destinationId: "d", message: Ping(seq: 1, text: "x"))
            XCTFail("expected send after close to throw")
        } catch {
            XCTAssertEqual(error as? NetworkError, .transportStopped)
        }
    }

    // ── InMemoryConnectivityMonitor ──────────────────────────────────────────

    func testConnectivityMonitorSnapshotAndState() {
        let ctx = NetworkContext(
            state: .online,
            preferredTransport: .wiFi,
            availableTransports: [.wiFi],
            signalStrengthDbm: -50,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 1,
            snapshotAt: Date())
        let monitor = InMemoryConnectivityMonitor(initial: ctx)
        XCTAssertEqual(monitor.currentState, .online)
        XCTAssertEqual(monitor.getSnapshot(), ctx)
    }

    func testConnectivityMonitorWatchEmitsCurrentThenChanges() async {
        let monitor = InMemoryConnectivityMonitor(initial: .offline)
        // watch() emits the current context immediately, then each publish.
        let stream = monitor.watch()
        let online = NetworkContext(
            state: .online,
            preferredTransport: .grpc,
            availableTransports: [.grpc],
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 0,
            snapshotAt: Date())
        monitor.publish(online)
        monitor.close()

        var states: [ConnectivityState] = []
        for await c in stream { states.append(c.state) }
        // Baseline (offline) then the published online.
        XCTAssertEqual(states, [.offline, .online])
        XCTAssertEqual(monitor.currentState, .online)
    }

    func testConnectivityMonitorMultipleWatchersFanOut() async {
        let monitor = InMemoryConnectivityMonitor(initial: .offline)
        let s1 = monitor.watch()
        let s2 = monitor.watch()
        XCTAssertEqual(monitor.watcherCount, 2)
        let meshOnly = NetworkContext(
            state: .meshOnly,
            preferredTransport: .aether,
            availableTransports: [.aether],
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 3,
            snapshotAt: Date())
        monitor.publish(meshOnly)
        monitor.close()

        var got1: [ConnectivityState] = []
        for await c in s1 { got1.append(c.state) }
        var got2: [ConnectivityState] = []
        for await c in s2 { got2.append(c.state) }
        XCTAssertEqual(got1, [.offline, .meshOnly])
        XCTAssertEqual(got2, [.offline, .meshOnly])
    }

    // ── IdentityPayloadOptimiser ─────────────────────────────────────────────

    func testPayloadOptimiserRoundTripPreservesData() async throws {
        let optimiser = IdentityPayloadOptimiser()
        let original = NetworkPayload.create(data: Data([1, 2, 3, 4, 5]))
        let optimised = try await optimiser.optimise(original, targetTransport: .bluetooth)
        // Tagged with the target transport, data unchanged.
        XCTAssertEqual(optimised.metadata[IdentityPayloadOptimiser.optimisedForKey],
                       String(TransportKind.bluetooth.rawValue))
        XCTAssertEqual(optimised.data, original.data)
        XCTAssertEqual(optimised.id, original.id)

        let restored = optimiser.decompress(optimised)
        XCTAssertNil(restored.metadata[IdentityPayloadOptimiser.optimisedForKey])
        XCTAssertEqual(restored.data, original.data)
    }

    // ── InMemoryPeerDiscovery ────────────────────────────────────────────────

    private func peer(_ id: String, _ role: PeerRole = .peer) -> PeerInfo {
        PeerInfo(nodeId: id, displayName: id, supportedTransports: [.wiFi],
                 role: role, signalStrengthDbm: -30, lastSeen: Date())
    }

    func testPeerDiscoveryAnnounceThenDiscover() async throws {
        let discovery = InMemoryPeerDiscovery()
        // discover() first (eager registration), then announce, then no explicit
        // close — so bound the stream by collecting exactly the announced count.
        let stream = discovery.discover()
        try await discovery.announce(localInfo: peer("n1"))
        try await discovery.announce(localInfo: peer("n2", .relay))

        var got: [String] = []
        for await p in stream {
            got.append(p.nodeId)
            if got.count == 2 { break }
        }
        XCTAssertEqual(got, ["n1", "n2"])
    }

    func testPeerDiscoveryReplaysAlreadyAnnounced() async throws {
        let discovery = InMemoryPeerDiscovery()
        // Announce BEFORE discover() — a late subscriber must still see them.
        try await discovery.announce(localInfo: peer("early-1"))
        try await discovery.announce(localInfo: peer("early-2"))

        let stream = discovery.discover()
        var got: [String] = []
        for await p in stream {
            got.append(p.nodeId)
            if got.count == 2 { break }
        }
        XCTAssertEqual(got, ["early-1", "early-2"])
    }

    // ── InMemoryMeshNetwork ──────────────────────────────────────────────────

    func testMeshNetworkLocalNodeAndPeers() async throws {
        let mesh = InMemoryMeshNetwork(localNodeId: "me", peers: ["a", "b"])
        XCTAssertEqual(mesh.localNodeId, "me")
        let peers = try await mesh.getPeerIds()
        XCTAssertEqual(peers, ["a", "b"])
    }

    func testMeshNetworkHealthReflectsPeerCount() async throws {
        let mesh = InMemoryMeshNetwork(localNodeId: "me")
        // No peers → offline health.
        let empty = try await mesh.getMeshHealth()
        XCTAssertEqual(empty.state, .offline)
        XCTAssertEqual(empty.nearbyPeerCount, 0)
        XCTAssertEqual(empty.preferredTransport, .localStore)

        mesh.addPeer("p1")
        mesh.addPeer("p2")
        mesh.addPeer("p1") // duplicate ignored
        let healthy = try await mesh.getMeshHealth()
        XCTAssertEqual(healthy.state, .meshOnly)
        XCTAssertEqual(healthy.nearbyPeerCount, 2)
        XCTAssertEqual(healthy.preferredTransport, .aether)
        XCTAssertEqual(healthy.availableTransports, [.aether])
    }

    func testMeshNetworkSetPeersReplaces() async throws {
        let mesh = InMemoryMeshNetwork(localNodeId: "me", peers: ["old"])
        mesh.setPeers(["x", "y", "z"])
        let peers = try await mesh.getPeerIds()
        XCTAssertEqual(peers, ["x", "y", "z"])
    }
}
