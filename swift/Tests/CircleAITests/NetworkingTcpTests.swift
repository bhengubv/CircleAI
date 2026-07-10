// NetworkingTcpTests.swift
//
// Validates the CircleAI.Networking.Tcp port (NetworkingTcp.swift): enum
// ordinals, record Codable, the TcpKnownPorts constants, the
// InMemoryTcpConnectionRegistry (default state + total-bytes-sent sum), the
// byte-exact 4-byte little-endian length-prefix framing (TcpFraming), and the
// TcpNetworkTransport wired to a deterministic loopback ITcpStreamSocket —
// availability, the framed write on the wire, send-before-open throwing, the send
// loopback, pre-subscription buffering, and stop→complete.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingTcpTests: XCTestCase {

    // ── A deterministic loopback TCP stream (the injected "socket") ───────────
    //
    // open() retains the inbound writer and flips connected; write() records the
    // framed bytes, then de-frames them (length prefix + data) and echoes the
    // payload back into the inbound stream (a loopback) so send → receive is
    // exercised with no NetworkStream.
    private final class LoopbackTcpSocket: ITcpStreamSocket, @unchecked Sendable {
        private let lock = NSLock()
        private var connected = false
        private var inbound: ITcpInboundWriter?
        private(set) var lastFramed: Data?

        init(connected: Bool = false) { self.connected = connected }

        var isConnected: Bool { lock.lock(); defer { lock.unlock() }; return connected }

        func open(inbound: ITcpInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; connected = true; lock.unlock()
        }

        func write(_ framed: Data) async throws {
            lock.lock(); lastFramed = framed; let sink = inbound; lock.unlock()
            // De-frame exactly as the C# pump would: 4-byte LE length + that many
            // bytes, then echo the payload back into the inbound stream.
            let len = TcpFraming.decodeLength(framed)
            let data = framed.subdata(in: 4..<(4 + len))
            sink?.push(NetworkPayload.create(data: data))
        }

        func close() async throws {
            lock.lock(); connected = false; lock.unlock()
        }
    }

    // ── TcpConnectionState ordinals ──────────────────────────────────────────

    func testConnectionStateOrdinals() {
        XCTAssertEqual(TcpConnectionState.disconnected.rawValue, 0)
        XCTAssertEqual(TcpConnectionState.connecting.rawValue,   1)
        XCTAssertEqual(TcpConnectionState.connected.rawValue,    2)
        XCTAssertEqual(TcpConnectionState.closing.rawValue,      3)
        XCTAssertEqual(TcpConnectionState.failed.rawValue,       4)
        XCTAssertEqual(TcpConnectionState.allCases.count,        5)
    }

    // ── TcpKnownPorts ────────────────────────────────────────────────────────

    func testKnownPorts() {
        XCTAssertEqual(TcpKnownPorts.http,    80)
        XCTAssertEqual(TcpKnownPorts.https,   443)
        XCTAssertEqual(TcpKnownPorts.ssh,     22)
        XCTAssertEqual(TcpKnownPorts.smtp,    25)
        XCTAssertEqual(TcpKnownPorts.imap,    143)
        XCTAssertEqual(TcpKnownPorts.imapSsl, 993)
        XCTAssertEqual(TcpKnownPorts.pop3,    110)
        XCTAssertEqual(TcpKnownPorts.pop3Ssl, 995)
        XCTAssertEqual(TcpKnownPorts.mqtt,    1883)
        XCTAssertEqual(TcpKnownPorts.mqttSsl, 8883)
    }

    func testEndpointDescriptorCodableRoundTrip() throws {
        let d = TcpEndpointDescriptor(host: "1.2.3.4", port: 443, noDelay: true,
                                      keepAlive: false, connectTimeout: 5)
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(TcpEndpointDescriptor.self, from: data)
        XCTAssertEqual(d, back)
    }

    // ── TcpFraming (byte-exact 4-byte little-endian length prefix) ────────────

    func testFramingLittleEndianLengthPrefix() {
        // A 3-byte payload → length 3 → LE prefix 03 00 00 00, then the bytes.
        let framed = TcpFraming.frame(Data([0xAA, 0xBB, 0xCC]))
        XCTAssertEqual([UInt8](framed), [0x03, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC])
    }

    func testFramingLengthPrefixForLargerLength() {
        // Length 258 = 0x0102 → LE prefix 02 01 00 00.
        let payload = Data(repeating: 0x7, count: 258)
        let framed = TcpFraming.frame(payload)
        XCTAssertEqual([UInt8](framed.prefix(4)), [0x02, 0x01, 0x00, 0x00])
        XCTAssertEqual(framed.count, 4 + 258)
    }

    func testFramingRoundTripDecode() {
        let framed = TcpFraming.frame(Data([1, 2, 3, 4, 5]))
        XCTAssertEqual(TcpFraming.decodeLength(framed), 5)
    }

    func testFramingEmptyPayload() {
        let framed = TcpFraming.frame(Data())
        XCTAssertEqual([UInt8](framed), [0x00, 0x00, 0x00, 0x00])
        XCTAssertEqual(TcpFraming.decodeLength(framed), 0)
    }

    // ── InMemoryTcpConnectionRegistry ────────────────────────────────────────

    func testRegistryDefaultStateAndGet() {
        let reg = InMemoryTcpConnectionRegistry()
        XCTAssertNil(reg.get("e1"))
        XCTAssertEqual(reg.state("e1"), .disconnected) // default
        let d = TcpEndpointDescriptor(host: "h", port: 1, noDelay: false, keepAlive: false, connectTimeout: 1)
        reg.register("e1", d)
        reg.setState("e1", .connected)
        XCTAssertEqual(reg.get("e1"), d)
        XCTAssertEqual(reg.state("e1"), .connected)
    }

    func testRegistryTotalBytesSent() {
        let reg = InMemoryTcpConnectionRegistry()
        XCTAssertEqual(reg.totalBytesSent("e1"), 0) // empty
        reg.recordSample(TcpThroughputSample(endpointId: "e1", bytesSent: 100, bytesReceived: 0, atUtc: Date()))
        reg.recordSample(TcpThroughputSample(endpointId: "e1", bytesSent: 250, bytesReceived: 0, atUtc: Date()))
        reg.recordSample(TcpThroughputSample(endpointId: "other", bytesSent: 999, bytesReceived: 0, atUtc: Date()))
        XCTAssertEqual(reg.totalBytesSent("e1"), 350)
    }

    // ── TcpNetworkTransport ──────────────────────────────────────────────────

    func testTransportKindAndAvailability() async throws {
        let socket = LoopbackTcpSocket()
        let t = TcpNetworkTransport(socket: socket)
        XCTAssertEqual(t.kind, .tcp)
        XCTAssertFalse(t.isAvailable) // not connected yet
        try await t.start()
        XCTAssertTrue(t.isAvailable)
    }

    func testTransportSendBeforeStartThrowsNotConnected() async throws {
        let t = TcpNetworkTransport(socket: LoopbackTcpSocket())
        do {
            try await t.send(NetworkPayload.create(data: Data([1])))
            XCTFail("expected send before start to throw")
        } catch {
            XCTAssertEqual(error as? NetworkError, .notConnected)
        }
    }

    func testTransportWritesFramedBytesToWire() async throws {
        let socket = LoopbackTcpSocket()
        let t = TcpNetworkTransport(socket: socket)
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([0xAA, 0xBB, 0xCC])))
        XCTAssertEqual([UInt8](socket.lastFramed ?? Data()), [0x03, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC])
    }

    func testTransportSendLoopsBackThroughSocket() async throws {
        let socket = LoopbackTcpSocket()
        let t = TcpNetworkTransport(socket: socket)
        try await t.start()
        let stream = t.receive()
        try await t.send(NetworkPayload.create(data: Data([1])))
        try await t.send(NetworkPayload.create(data: Data([2, 3])))
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([1]), Data([2, 3])])
    }

    func testTransportBuffersInboundPushedBeforeReceive() async throws {
        let socket = LoopbackTcpSocket()
        let t = TcpNetworkTransport(socket: socket)
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([42]))) // before receive()
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([42])])
    }

    func testTransportReceiveFinishesAfterStop() async throws {
        let t = TcpNetworkTransport(socket: LoopbackTcpSocket())
        try await t.start()
        try await t.stop()
        let stream = t.receive()
        var count = 0
        for await _ in stream { count += 1 }
        XCTAssertEqual(count, 0)
    }
}
