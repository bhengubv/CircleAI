// NetworkingNearLinkTests.swift
//
// Validates the CircleAI.Networking.NearLink port (NetworkingNearLink.swift):
// enum ordinals, record Codable round-trips, the in-memory registry (device
// ordering, default pairing state, session open/close, and the RSSI average with
// its -127 empty default), and the NearLinkTransport wired to a deterministic
// in-memory INearLinkAdapter — including the adapter→inbound push path,
// pre-subscription buffering, and the stop→complete ordering.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingNearLinkTests: XCTestCase {

    // ── A deterministic loopback adapter (the injected "socket") ──────────────
    //
    // On start it retains the inbound writer; send() echoes the payload straight
    // back into the inbound stream (a loopback), so send → receive is exercised
    // with no radio.
    private final class LoopbackNearLinkAdapter: INearLinkAdapter, @unchecked Sendable {
        private let lock = NSLock()
        private var available: Bool
        private var inbound: INearLinkInboundWriter?
        private(set) var startCount = 0
        private(set) var stopCount = 0

        init(available: Bool = true) { self.available = available }

        var isAvailable: Bool { lock.lock(); defer { lock.unlock() }; return available }

        func start(inbound: INearLinkInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; startCount += 1; lock.unlock()
        }

        func stop() async throws {
            lock.lock(); stopCount += 1; lock.unlock()
        }

        func send(_ payload: NetworkPayload) async throws {
            lock.lock(); let sink = inbound; lock.unlock()
            sink?.push(payload)  // loopback: echo to the inbound stream
        }
    }

    // ── Enum ordinals ────────────────────────────────────────────────────────

    func testPairingStateOrdinals() {
        XCTAssertEqual(NearLinkPairingState.unpaired.rawValue,       0)
        XCTAssertEqual(NearLinkPairingState.pairing.rawValue,        1)
        XCTAssertEqual(NearLinkPairingState.paired.rawValue,         2)
        XCTAssertEqual(NearLinkPairingState.pairingFailed.rawValue,  3)
        XCTAssertEqual(NearLinkPairingState.allCases.count,          4)
    }

    func testPowerProfileOrdinals() {
        XCTAssertEqual(NearLinkPowerProfile.lowEnergy.rawValue,      0)
        XCTAssertEqual(NearLinkPowerProfile.balanced.rawValue,       1)
        XCTAssertEqual(NearLinkPowerProfile.highThroughput.rawValue, 2)
        XCTAssertEqual(NearLinkPowerProfile.allCases.count,          3)
    }

    // ── Record Codable ───────────────────────────────────────────────────────

    func testDeviceCodableRoundTrip() throws {
        let d = NearLinkDevice(deviceId: "d1", friendlyName: "Band", manufacturerId: "HW", firmwareVersion: "1.2")
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(NearLinkDevice.self, from: data)
        XCTAssertEqual(d, back)
    }

    func testSessionCodableRoundTrip() throws {
        let s = NearLinkSession(sessionId: "s1", deviceId: "d1", powerProfile: .highThroughput,
                                startedUtc: Date(timeIntervalSince1970: 100))
        let data = try JSONEncoder().encode(s)
        let back = try JSONDecoder().decode(NearLinkSession.self, from: data)
        XCTAssertEqual(s, back)
    }

    // ── InMemoryNearLinkRegistry ─────────────────────────────────────────────

    func testRegistryDevicesSortedByFriendlyName() {
        let reg = InMemoryNearLinkRegistry()
        reg.register(NearLinkDevice(deviceId: "3", friendlyName: "Zeta", manufacturerId: "", firmwareVersion: ""))
        reg.register(NearLinkDevice(deviceId: "1", friendlyName: "Alpha", manufacturerId: "", firmwareVersion: ""))
        reg.register(NearLinkDevice(deviceId: "2", friendlyName: "Mid", manufacturerId: "", firmwareVersion: ""))
        XCTAssertEqual(reg.allDevices.map { $0.friendlyName }, ["Alpha", "Mid", "Zeta"])
        XCTAssertEqual(reg.getDevice("2")?.friendlyName, "Mid")
    }

    func testRegistryPairingStateDefaultsToUnpaired() {
        let reg = InMemoryNearLinkRegistry()
        XCTAssertEqual(reg.pairingState("unknown"), .unpaired)
        reg.setPairingState("d", .paired)
        XCTAssertEqual(reg.pairingState("d"), .paired)
    }

    func testRegistrySessionOpenGetClose() {
        let reg = InMemoryNearLinkRegistry()
        let s = NearLinkSession(sessionId: "s1", deviceId: "d1", powerProfile: .balanced, startedUtc: Date())
        reg.openSession(s)
        XCTAssertEqual(reg.getSession("s1"), s)
        XCTAssertEqual(reg.activeSessions.count, 1)
        reg.closeSession("s1")
        XCTAssertNil(reg.getSession("s1"))
        XCTAssertTrue(reg.activeSessions.isEmpty)
    }

    func testRegistryAvgRssiEmptyDefaultsToMinus127() {
        let reg = InMemoryNearLinkRegistry()
        XCTAssertEqual(reg.avgRssi("d"), -127, accuracy: 0.0001) // empty → -127
        reg.recordThroughput(NearLinkThroughputSample(deviceId: "d", kbpsRead: 1, kbpsWrite: 1, rssiDbm: -40, atUtc: Date()))
        reg.recordThroughput(NearLinkThroughputSample(deviceId: "d", kbpsRead: 1, kbpsWrite: 1, rssiDbm: -60, atUtc: Date()))
        reg.recordThroughput(NearLinkThroughputSample(deviceId: "other", kbpsRead: 1, kbpsWrite: 1, rssiDbm: -99, atUtc: Date()))
        XCTAssertEqual(reg.avgRssi("d"), -50, accuracy: 0.0001)
    }

    // ── NearLinkTransport ────────────────────────────────────────────────────

    func testTransportKindAndAvailability() {
        let up = NearLinkTransport(adapter: LoopbackNearLinkAdapter(available: true))
        XCTAssertEqual(up.kind, .nearLink)
        XCTAssertTrue(up.isAvailable)

        let down = NearLinkTransport(adapter: LoopbackNearLinkAdapter(available: false))
        XCTAssertFalse(down.isAvailable)
    }

    func testTransportStartHandsAdapterInboundAndSendLoopsBack() async throws {
        let adapter = LoopbackNearLinkAdapter()
        let t = NearLinkTransport(adapter: adapter)
        try await t.start()
        XCTAssertEqual(adapter.startCount, 1)

        let stream = t.receive()
        try await t.send(NetworkPayload.create(data: Data([1])))
        try await t.send(NetworkPayload.create(data: Data([2])))
        try await t.stop()
        XCTAssertEqual(adapter.stopCount, 1)

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([1]), Data([2])])
    }

    func testTransportBuffersInboundPushedBeforeReceive() async throws {
        let adapter = LoopbackNearLinkAdapter()
        let t = NearLinkTransport(adapter: adapter)
        try await t.start()
        // Adapter pushes (via send loopback) BEFORE receive() is called.
        try await t.send(NetworkPayload.create(data: Data([42])))
        let stream = t.receive()
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([42])])
    }

    func testTransportReceiveFinishesAfterStop() async throws {
        let t = NearLinkTransport(adapter: LoopbackNearLinkAdapter())
        try await t.start()
        try await t.stop()
        let stream = t.receive()
        var count = 0
        for await _ in stream { count += 1 }
        XCTAssertEqual(count, 0)
    }
}
