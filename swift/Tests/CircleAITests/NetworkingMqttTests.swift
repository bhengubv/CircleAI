// NetworkingMqttTests.swift
//
// Validates the CircleAI.Networking.Mqtt port (NetworkingMqtt.swift): the MqttQos
// wire values, record Codable, the InMemoryMqttBroker (connect/disconnect,
// subscription argument guards, the topic-filter `Matches` algorithm incl. `#`
// and `+`, retained store, and matching subscribers), and the MqttNetworkTransport
// wired to a deterministic loopback IMqttClientSocket — availability, the
// subscribe-on-start topic, the publish topic + QoS selection, the send loopback,
// pre-subscription buffering, and stop→complete.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingMqttTests: XCTestCase {

    // ── A deterministic loopback MQTT client (the injected "socket") ──────────
    //
    // connect() retains the inbound writer and flips connected; publish() echoes
    // the published payload back into the inbound stream (a loopback) so
    // send → receive is exercised with no broker. Records the last publish + the
    // subscribed topics for assertions.
    private final class LoopbackMqttSocket: IMqttClientSocket, @unchecked Sendable {
        private let lock = NSLock()
        private var connected = false
        private var inbound: IMqttInboundWriter?
        private(set) var subscribedTopics: [String] = []
        private(set) var lastPublish: MqttPublishRequest?

        var isConnected: Bool { lock.lock(); defer { lock.unlock() }; return connected }

        func connect(inbound: IMqttInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; connected = true; lock.unlock()
        }

        func subscribe(_ topicFilter: String) async throws {
            lock.lock(); subscribedTopics.append(topicFilter); lock.unlock()
        }

        func publish(_ request: MqttPublishRequest) async throws {
            lock.lock(); lastPublish = request; let sink = inbound; lock.unlock()
            sink?.push(NetworkPayload.create(data: request.payload)) // loopback
        }

        func disconnect() async throws {
            lock.lock(); connected = false; lock.unlock()
        }
    }

    // ── MqttQos wire values ──────────────────────────────────────────────────

    func testQosWireValues() {
        XCTAssertEqual(MqttQos.atMostOnce.rawValue,  0)
        XCTAssertEqual(MqttQos.atLeastOnce.rawValue, 1)
        XCTAssertEqual(MqttQos.exactlyOnce.rawValue, 2)
        XCTAssertEqual(MqttQos.allCases.count,       3)
    }

    func testTopicDescriptorCodableRoundTrip() throws {
        let d = MqttTopicDescriptor(topic: "circle/payloads/x", qos: .exactlyOnce)
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(MqttTopicDescriptor.self, from: data)
        XCTAssertEqual(d, back)
    }

    func testRetainedMessageCodableRoundTrip() throws {
        let m = MqttRetainedMessage(topic: "t", payload: Data([1, 2, 3]),
                                    retainedAtUtc: Date(timeIntervalSince1970: 5))
        let data = try JSONEncoder().encode(m)
        let back = try JSONDecoder().decode(MqttRetainedMessage.self, from: data)
        XCTAssertEqual(m, back)
    }

    // ── InMemoryMqttBroker ───────────────────────────────────────────────────

    func testBrokerConnectDisconnect() {
        let b = InMemoryMqttBroker()
        b.connect(MqttClientDescriptor(clientId: "c1", host: "h", port: 1883, useTls: false, keepAlive: 60))
        b.connect(MqttClientDescriptor(clientId: "c2", host: "h", port: 1883, useTls: false, keepAlive: 60))
        XCTAssertEqual(b.connectedClients.count, 2)
        b.disconnect("c1")
        XCTAssertEqual(b.connectedClients.map { $0.clientId }, ["c2"])
    }

    func testBrokerSubscribeRejectsBlankArgs() {
        let b = InMemoryMqttBroker()
        XCTAssertThrowsError(try b.subscribe("", "topic")) { error in
            XCTAssertEqual(error as? MqttBrokerError, .argument("clientId required"))
        }
        XCTAssertThrowsError(try b.subscribe("c1", "   ")) { error in
            XCTAssertEqual(error as? MqttBrokerError, .argument("topicFilter required"))
        }
    }

    func testBrokerMatchesExactAndWildcards() {
        let b = InMemoryMqttBroker()
        // Exact match.
        XCTAssertTrue(b.matches("a/b/c", "a/b/c"))
        XCTAssertFalse(b.matches("a/b/c", "a/b/d"))
        // Length must match without a '#'.
        XCTAssertFalse(b.matches("a/b", "a/b/c"))
        XCTAssertFalse(b.matches("a/b/c", "a/b"))
        // '+' single-level wildcard.
        XCTAssertTrue(b.matches("a/b/c", "a/+/c"))
        XCTAssertTrue(b.matches("a/x/c", "a/+/c"))
        XCTAssertFalse(b.matches("a/b/c/d", "a/+/c")) // still length-checked
        // '#' multi-level wildcard matches the rest.
        XCTAssertTrue(b.matches("a/b/c", "a/#"))
        XCTAssertTrue(b.matches("a/b/c/d/e", "a/#"))
        XCTAssertTrue(b.matches("a/b/c", "#"))
        // Empty topic or filter → false.
        XCTAssertFalse(b.matches("", "a/#"))
        XCTAssertFalse(b.matches("a/b", ""))
    }

    func testBrokerRetainedStore() {
        let b = InMemoryMqttBroker()
        XCTAssertNil(b.getRetained("t"))
        let m = MqttRetainedMessage(topic: "t", payload: Data([9]), retainedAtUtc: Date())
        b.publishRetained(m)
        XCTAssertEqual(b.getRetained("t"), m)
    }

    func testBrokerMatchingSubscribers() throws {
        let b = InMemoryMqttBroker()
        try b.subscribe("c1", "circle/payloads/c1/#")
        try b.subscribe("c2", "circle/+/broadcast")
        try b.subscribe("c3", "other/#")
        XCTAssertEqual(Set(b.matchingSubscribers("circle/payloads/c1/msg")), ["c1"])
        XCTAssertEqual(Set(b.matchingSubscribers("circle/payloads/broadcast")), ["c2"])
        XCTAssertTrue(b.matchingSubscribers("nothing/here").isEmpty)
    }

    // ── MqttNetworkTransport ─────────────────────────────────────────────────

    func testTransportKindAndAvailability() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        XCTAssertEqual(t.kind, .mqtt)
        XCTAssertFalse(t.isAvailable) // not connected yet
        try await t.start()
        XCTAssertTrue(t.isAvailable)
    }

    func testTransportSubscribesToLocalTopicOnStart() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        XCTAssertEqual(t.subscriptionTopic, "circle/payloads/node-1/#")
        try await t.start()
        XCTAssertEqual(socket.subscribedTopics, ["circle/payloads/node-1/#"])
    }

    func testTransportPublishTopicForDestination() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([1]), destinationId: "peer-9"))
        XCTAssertEqual(socket.lastPublish?.topic, "circle/payloads/peer-9")
    }

    func testTransportPublishTopicForBroadcast() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([1]))) // no destination
        XCTAssertEqual(socket.lastPublish?.topic, "circle/payloads/broadcast")
    }

    func testTransportQosSelection() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        try await t.start()
        // Normal < High → AtLeastOnce.
        try await t.send(NetworkPayload.create(data: Data([1]), priority: .normal))
        XCTAssertEqual(socket.lastPublish?.qos, .atLeastOnce)
        // High → ExactlyOnce.
        try await t.send(NetworkPayload.create(data: Data([1]), priority: .high))
        XCTAssertEqual(socket.lastPublish?.qos, .exactlyOnce)
        // Emergency (> High) → ExactlyOnce.
        try await t.send(NetworkPayload.create(data: Data([1]), priority: .emergency))
        XCTAssertEqual(socket.lastPublish?.qos, .exactlyOnce)
    }

    func testTransportSendLoopsBackThroughSocket() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        try await t.start()
        let stream = t.receive()
        try await t.send(NetworkPayload.create(data: Data([1])))
        try await t.send(NetworkPayload.create(data: Data([2])))
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([1]), Data([2])])
    }

    func testTransportBuffersInboundPushedBeforeReceive() async throws {
        let socket = LoopbackMqttSocket()
        let t = MqttNetworkTransport(socket: socket, clientId: "node-1")
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([42]))) // before receive()
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([42])])
    }

    func testTransportReceiveFinishesAfterStop() async throws {
        let t = MqttNetworkTransport(socket: LoopbackMqttSocket(), clientId: "n")
        try await t.start()
        try await t.stop()
        let stream = t.receive()
        var count = 0
        for await _ in stream { count += 1 }
        XCTAssertEqual(count, 0)
    }
}
