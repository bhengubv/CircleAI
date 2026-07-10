// NetworkingWiFiTests.swift
//
// Validates the CircleAI.Networking.WiFi port (NetworkingWiFi.swift): the port
// constants, the IPAddress.TryParse analogue used to pick unicast-vs-broadcast,
// the destination resolution (unicast to a parseable IP, else broadcast), the
// WiFiNetworkTransport wired to a deterministic loopback IUdpSocket, and the
// WiFiPeerDiscovery beacon parse/announce over a deterministic loopback
// IUdpBeaconSocket.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingWiFiTests: XCTestCase {

    // ── A deterministic loopback UDP socket (the injected "socket") ───────────
    //
    // open() retains the inbound writer and flips bound; send() records the
    // destination + data, then echoes the data back into the inbound stream (a
    // loopback) so send → receive is exercised with no UdpClient.
    private final class LoopbackUdpSocket: IUdpSocket, @unchecked Sendable {
        private let lock = NSLock()
        private var bound = false
        private var inbound: IUdpInboundWriter?
        private(set) var lastDestination: UdpDestination?
        private(set) var lastData: Data?

        var isBound: Bool { lock.lock(); defer { lock.unlock() }; return bound }

        func open(inbound: IUdpInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; bound = true; lock.unlock()
        }

        func send(_ data: Data, to destination: UdpDestination) async throws {
            lock.lock(); lastDestination = destination; lastData = data; let sink = inbound; lock.unlock()
            sink?.push(NetworkPayload.create(data: data)) // loopback
        }

        func close() async throws {
            lock.lock(); bound = false; lock.unlock()
        }
    }

    // ── A deterministic loopback beacon socket ────────────────────────────────
    //
    // Feeds a fixed list of datagrams to receiveBeacons(); records broadcasts.
    private final class LoopbackBeaconSocket: IUdpBeaconSocket, @unchecked Sendable {
        private let lock = NSLock()
        private let datagrams: [UdpBeaconDatagram]
        private(set) var broadcasts: [Data] = []

        init(datagrams: [UdpBeaconDatagram]) { self.datagrams = datagrams }

        func receiveBeacons() -> AsyncStream<UdpBeaconDatagram> {
            let items = datagrams
            return AsyncStream(bufferingPolicy: .unbounded) { continuation in
                for d in items { continuation.yield(d) }
                continuation.finish()
            }
        }

        func broadcast(_ payload: Data) async throws {
            lock.lock(); broadcasts.append(payload); lock.unlock()
        }
    }

    // ── Port constants ───────────────────────────────────────────────────────

    func testPortConstants() {
        XCTAssertEqual(WiFiNetworkTransport.discoveryPort, 47890)
        XCTAssertEqual(WiFiNetworkTransport.dataPort,      47891)
        XCTAssertEqual(WiFiNetworkTransport.broadcastAddress, "255.255.255.255")
    }

    // ── IPAddressParsing (unicast-vs-broadcast branch) ────────────────────────

    func testIPAddressParsingIPv4() {
        XCTAssertTrue(IPAddressParsing.isValid("192.168.1.10"))
        XCTAssertTrue(IPAddressParsing.isValid("0.0.0.0"))
        XCTAssertTrue(IPAddressParsing.isValid("255.255.255.255"))
        XCTAssertFalse(IPAddressParsing.isValid("256.1.1.1"))   // octet > 255
        XCTAssertFalse(IPAddressParsing.isValid("1.2.3"))       // too few octets
        XCTAssertFalse(IPAddressParsing.isValid("1.2.3.4.5"))   // too many octets
        XCTAssertFalse(IPAddressParsing.isValid("a.b.c.d"))     // non-numeric
        XCTAssertFalse(IPAddressParsing.isValid("peer-9"))      // a node id, not an IP
        XCTAssertFalse(IPAddressParsing.isValid(""))
    }

    func testIPAddressParsingIPv6() {
        XCTAssertTrue(IPAddressParsing.isValid("2001:0db8:0000:0000:0000:0000:0000:0001"))
        XCTAssertTrue(IPAddressParsing.isValid("fe80::1"))
        XCTAssertTrue(IPAddressParsing.isValid("::1"))
        XCTAssertFalse(IPAddressParsing.isValid("gggg::1"))     // non-hex group
        XCTAssertFalse(IPAddressParsing.isValid("1:2:3"))       // too few, no "::"
    }

    // ── Destination resolution ───────────────────────────────────────────────

    func testResolveDestinationUnicastForParseableIp() {
        let payload = NetworkPayload.create(data: Data([1]), destinationId: "10.0.0.5")
        let dest = WiFiNetworkTransport.resolveDestination(for: payload)
        XCTAssertEqual(dest.host, "10.0.0.5")
        XCTAssertEqual(dest.port, WiFiNetworkTransport.dataPort)
        XCTAssertFalse(dest.isBroadcast)
    }

    func testResolveDestinationBroadcastForNonIp() {
        let payload = NetworkPayload.create(data: Data([1]), destinationId: "peer-9")
        let dest = WiFiNetworkTransport.resolveDestination(for: payload)
        XCTAssertEqual(dest.host, "255.255.255.255")
        XCTAssertEqual(dest.port, WiFiNetworkTransport.dataPort)
        XCTAssertTrue(dest.isBroadcast)
    }

    func testResolveDestinationBroadcastForNoDestination() {
        let payload = NetworkPayload.create(data: Data([1])) // nil destination
        let dest = WiFiNetworkTransport.resolveDestination(for: payload)
        XCTAssertTrue(dest.isBroadcast)
        XCTAssertEqual(dest.host, "255.255.255.255")
    }

    // ── WiFiNetworkTransport ─────────────────────────────────────────────────

    func testTransportKindAndAvailability() async throws {
        let socket = LoopbackUdpSocket()
        let t = WiFiNetworkTransport(socket: socket)
        XCTAssertEqual(t.kind, .wiFi)
        XCTAssertFalse(t.isAvailable) // not bound yet
        try await t.start()
        XCTAssertTrue(t.isAvailable)
    }

    func testTransportSendUsesResolvedDestination() async throws {
        let socket = LoopbackUdpSocket()
        let t = WiFiNetworkTransport(socket: socket)
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([1]), destinationId: "10.0.0.5"))
        XCTAssertEqual(socket.lastDestination?.host, "10.0.0.5")
        XCTAssertEqual(socket.lastDestination?.isBroadcast, false)

        try await t.send(NetworkPayload.create(data: Data([2]), destinationId: "not-an-ip"))
        XCTAssertEqual(socket.lastDestination?.host, "255.255.255.255")
        XCTAssertEqual(socket.lastDestination?.isBroadcast, true)
    }

    func testTransportSendLoopsBackThroughSocket() async throws {
        let socket = LoopbackUdpSocket()
        let t = WiFiNetworkTransport(socket: socket)
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
        let socket = LoopbackUdpSocket()
        let t = WiFiNetworkTransport(socket: socket)
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([42]))) // before receive()
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([42])])
    }

    func testTransportReceiveFinishesAfterStop() async throws {
        let t = WiFiNetworkTransport(socket: LoopbackUdpSocket())
        try await t.start()
        try await t.stop()
        let stream = t.receive()
        var count = 0
        for await _ in stream { count += 1 }
        XCTAssertEqual(count, 0)
    }

    // ── WiFiPeerDiscovery ────────────────────────────────────────────────────

    func testDiscoveryBeaconMagicConstant() {
        XCTAssertEqual(WiFiPeerDiscovery.beaconMagic, "CIRCLEAI:BEACON:")
    }

    func testDiscoverParsesBeaconsAndSkipsNonBeacons() async throws {
        let socket = LoopbackBeaconSocket(datagrams: [
            UdpBeaconDatagram(message: "CIRCLEAI:BEACON:node-a", senderAddress: "10.0.0.1"),
            UdpBeaconDatagram(message: "garbage", senderAddress: "10.0.0.2"),
            UdpBeaconDatagram(message: "CIRCLEAI:BEACON:node-b", senderAddress: "10.0.0.3"),
        ])
        let discovery = WiFiPeerDiscovery(socket: socket)

        var peers: [PeerInfo] = []
        for await p in discovery.discover() { peers.append(p) }

        XCTAssertEqual(peers.map { $0.nodeId }, ["node-a", "node-b"])
        XCTAssertEqual(peers[0].displayName, "WiFi/10.0.0.1")
        XCTAssertEqual(peers[0].supportedTransports, [.wiFi])
        XCTAssertEqual(peers[0].role, .peer)
        XCTAssertNil(peers[0].signalStrengthDbm)
        XCTAssertEqual(peers[1].displayName, "WiFi/10.0.0.3")
    }

    func testAnnounceBroadcastsBeacon() async throws {
        let socket = LoopbackBeaconSocket(datagrams: [])
        let discovery = WiFiPeerDiscovery(socket: socket)
        let local = PeerInfo(nodeId: "me-1", displayName: nil, supportedTransports: [.wiFi],
                             role: .peer, signalStrengthDbm: nil, lastSeen: Date())
        try await discovery.announce(localInfo: local)

        XCTAssertEqual(socket.broadcasts.count, 1)
        let sent = String(decoding: socket.broadcasts[0], as: UTF8.self)
        XCTAssertEqual(sent, "CIRCLEAI:BEACON:me-1")
    }
}
