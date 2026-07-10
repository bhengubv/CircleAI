// NetworkingWebSocketTests.swift
//
// Validates the CircleAI.Networking.WebSocket port (NetworkingWebSocket.swift):
// enum ordinals (incl. the C# `Closed_Error` → `closedError` ordinal 5), record
// Codable, the InMemoryWebSocketSessionRegistry (default state, total-bytes, and
// per-type frame counts), and the WebSocketTransport wired to a deterministic
// loopback IWebSocketSocket — availability, the binary send loopback,
// pre-subscription buffering, and stop→complete.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingWebSocketTests: XCTestCase {

    // ── A deterministic loopback WebSocket (the injected "socket") ────────────
    //
    // connect() retains the inbound writer and flips open; send() echoes the data
    // back into the inbound stream (a loopback) so send → receive is exercised
    // with no ClientWebSocket.
    private final class LoopbackWebSocket: IWebSocketSocket, @unchecked Sendable {
        private let lock = NSLock()
        private var open = false
        private var inbound: IWebSocketInboundWriter?
        private(set) var lastSent: Data?

        var isOpen: Bool { lock.lock(); defer { lock.unlock() }; return open }

        func connect(inbound: IWebSocketInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; open = true; lock.unlock()
        }

        func send(_ data: Data) async throws {
            lock.lock(); lastSent = data; let sink = inbound; lock.unlock()
            sink?.push(NetworkPayload.create(data: data)) // loopback
        }

        func close() async throws {
            lock.lock(); open = false; lock.unlock()
        }
    }

    // ── Enum ordinals ────────────────────────────────────────────────────────

    func testLinkStateOrdinals() {
        XCTAssertEqual(WebSocketLinkState.closed.rawValue,        0)
        XCTAssertEqual(WebSocketLinkState.connecting.rawValue,    1)
        XCTAssertEqual(WebSocketLinkState.open.rawValue,          2)
        XCTAssertEqual(WebSocketLinkState.closeSent.rawValue,     3)
        XCTAssertEqual(WebSocketLinkState.closeReceived.rawValue, 4)
        // C# `Closed_Error` member, ordinal 5.
        XCTAssertEqual(WebSocketLinkState.closedError.rawValue,   5)
        XCTAssertEqual(WebSocketLinkState.allCases.count,         6)
    }

    func testMessageTypeOrdinals() {
        XCTAssertEqual(WebSocketMessageType.text.rawValue,   0)
        XCTAssertEqual(WebSocketMessageType.binary.rawValue, 1)
        XCTAssertEqual(WebSocketMessageType.ping.rawValue,   2)
        XCTAssertEqual(WebSocketMessageType.pong.rawValue,   3)
        XCTAssertEqual(WebSocketMessageType.close.rawValue,  4)
        XCTAssertEqual(WebSocketMessageType.allCases.count,  5)
    }

    // ── Record Codable ───────────────────────────────────────────────────────

    func testEndpointDescriptorCodableRoundTrip() throws {
        let d = WebSocketEndpointDescriptor(uri: "wss://x/ws", headers: ["A": "1"],
                                            pingInterval: 30, subprotocols: ["p1", "p2"])
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(WebSocketEndpointDescriptor.self, from: data)
        XCTAssertEqual(d, back)
    }

    func testFrameSummaryCodableRoundTrip() throws {
        let f = WebSocketFrameSummary(sessionId: "s1", type: .binary, bytes: 42,
                                      atUtc: Date(timeIntervalSince1970: 7))
        let data = try JSONEncoder().encode(f)
        let back = try JSONDecoder().decode(WebSocketFrameSummary.self, from: data)
        XCTAssertEqual(f, back)
    }

    // ── InMemoryWebSocketSessionRegistry ─────────────────────────────────────

    func testRegistryDefaultStateAndGet() {
        let reg = InMemoryWebSocketSessionRegistry()
        XCTAssertNil(reg.get("s1"))
        XCTAssertEqual(reg.state("s1"), .closed) // default
        let d = WebSocketEndpointDescriptor(uri: "wss://x", headers: nil, pingInterval: 10, subprotocols: [])
        reg.register("s1", d)
        reg.setState("s1", .open)
        XCTAssertEqual(reg.get("s1"), d)
        XCTAssertEqual(reg.state("s1"), .open)
    }

    func testRegistryTotalBytesAndFrameCount() {
        let reg = InMemoryWebSocketSessionRegistry()
        XCTAssertEqual(reg.totalBytes("s1"), 0)
        XCTAssertEqual(reg.frameCount("s1", .binary), 0)
        reg.recordFrame(WebSocketFrameSummary(sessionId: "s1", type: .binary, bytes: 100, atUtc: Date()))
        reg.recordFrame(WebSocketFrameSummary(sessionId: "s1", type: .binary, bytes: 50, atUtc: Date()))
        reg.recordFrame(WebSocketFrameSummary(sessionId: "s1", type: .ping, bytes: 4, atUtc: Date()))
        reg.recordFrame(WebSocketFrameSummary(sessionId: "other", type: .binary, bytes: 999, atUtc: Date()))
        XCTAssertEqual(reg.totalBytes("s1"), 154)
        XCTAssertEqual(reg.frameCount("s1", .binary), 2)
        XCTAssertEqual(reg.frameCount("s1", .ping), 1)
        XCTAssertEqual(reg.frameCount("s1", .close), 0)
    }

    // ── WebSocketTransport ───────────────────────────────────────────────────

    func testTransportKindAndAvailability() async throws {
        let socket = LoopbackWebSocket()
        let t = WebSocketTransport(socket: socket, endpoint: "wss://x/ws")
        XCTAssertEqual(t.kind, .webSocket)
        XCTAssertEqual(t.endpointUri, "wss://x/ws")
        XCTAssertFalse(t.isAvailable) // not open yet
        try await t.start()
        XCTAssertTrue(t.isAvailable)
    }

    func testTransportSendTransmitsBinaryAndLoopsBack() async throws {
        let socket = LoopbackWebSocket()
        let t = WebSocketTransport(socket: socket, endpoint: "wss://x")
        try await t.start()
        let stream = t.receive()
        try await t.send(NetworkPayload.create(data: Data([1])))
        try await t.send(NetworkPayload.create(data: Data([2, 3])))
        XCTAssertEqual(socket.lastSent, Data([2, 3]))
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([1]), Data([2, 3])])
    }

    func testTransportBuffersInboundPushedBeforeReceive() async throws {
        let socket = LoopbackWebSocket()
        let t = WebSocketTransport(socket: socket, endpoint: "wss://x")
        try await t.start()
        try await t.send(NetworkPayload.create(data: Data([42]))) // before receive()
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([42])])
    }

    func testTransportReceiveFinishesAfterStop() async throws {
        let t = WebSocketTransport(socket: LoopbackWebSocket(), endpoint: "wss://x")
        try await t.start()
        try await t.stop()
        let stream = t.receive()
        var count = 0
        for await _ in stream { count += 1 }
        XCTAssertEqual(count, 0)
    }
}
