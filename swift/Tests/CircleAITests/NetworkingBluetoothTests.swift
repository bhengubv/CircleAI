// NetworkingBluetoothTests.swift
//
// Validates the CircleAI.Networking.Bluetooth port (NetworkingBluetooth.swift):
// enum ordinals, record Codable round-trips, the capability presets, the
// in-memory registry (ordering + default state + throughput average), and the
// BluetoothNetworkTransport wired to a deterministic in-memory IBleGattAdapter —
// including the adapter→inbound push path, pre-subscription buffering, and the
// stop→complete ordering.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingBluetoothTests: XCTestCase {

    // ── A deterministic loopback adapter (the injected "socket") ──────────────
    //
    // On start it retains the inbound writer; write() echoes the payload straight
    // back into the inbound stream (a loopback), so send → receive is exercised
    // with no radio.
    private final class LoopbackBleAdapter: IBleGattAdapter, @unchecked Sendable {
        private let lock = NSLock()
        private var available: Bool
        private var inbound: IBleInboundWriter?
        private(set) var startCount = 0
        private(set) var stopCount = 0

        init(available: Bool = true) { self.available = available }

        var isAvailable: Bool { lock.lock(); defer { lock.unlock() }; return available }

        func start(inbound: IBleInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; startCount += 1; lock.unlock()
        }

        func stop() async throws {
            lock.lock(); stopCount += 1; lock.unlock()
        }

        func write(_ payload: NetworkPayload) async throws {
            lock.lock(); let sink = inbound; lock.unlock()
            sink?.push(payload)  // loopback: echo to the inbound stream
        }
    }

    // ── BluetoothConnectionState ordinals ────────────────────────────────────

    func testConnectionStateOrdinals() {
        XCTAssertEqual(BluetoothConnectionState.disconnected.rawValue, 0)
        XCTAssertEqual(BluetoothConnectionState.discovering.rawValue,  1)
        XCTAssertEqual(BluetoothConnectionState.connecting.rawValue,   2)
        XCTAssertEqual(BluetoothConnectionState.connected.rawValue,    3)
        XCTAssertEqual(BluetoothConnectionState.failed.rawValue,       4)
        XCTAssertEqual(BluetoothConnectionState.allCases.count,        5)
    }

    // ── Capability presets (values match C# exactly) ─────────────────────────

    func testCapabilityProfilePresets() {
        XCTAssertEqual(BluetoothCapabilityProfiles.le5.maxMtuBytes, 247)
        XCTAssertTrue(BluetoothCapabilityProfiles.le5.supportsHighSpeed)
        XCTAssertEqual(BluetoothCapabilityProfiles.le5.compatibleProfiles, ["GATT", "L2CAP"])

        XCTAssertEqual(BluetoothCapabilityProfiles.le4.maxMtuBytes, 23)
        XCTAssertFalse(BluetoothCapabilityProfiles.le4.supportsHighSpeed)
        XCTAssertEqual(BluetoothCapabilityProfiles.le4.compatibleProfiles, ["GATT"])

        XCTAssertEqual(BluetoothCapabilityProfiles.classic.maxMtuBytes, 1024)
        XCTAssertEqual(BluetoothCapabilityProfiles.classic.compatibleProfiles, ["SPP", "RFCOMM"])
    }

    func testEndpointDescriptorCodableRoundTrip() throws {
        let e = BluetoothEndpointDescriptor(deviceId: "d1", name: "Watch",
                                            macAddress: "AA:BB:CC", advertisedServices: ["GATT"])
        let data = try JSONEncoder().encode(e)
        let back = try JSONDecoder().decode(BluetoothEndpointDescriptor.self, from: data)
        XCTAssertEqual(e, back)
    }

    // ── InMemoryBluetoothTransportRegistry ───────────────────────────────────

    func testRegistryEndpointsSortedByName() {
        let reg = InMemoryBluetoothTransportRegistry()
        reg.register(BluetoothEndpointDescriptor(deviceId: "3", name: "Zeta", macAddress: "", advertisedServices: []))
        reg.register(BluetoothEndpointDescriptor(deviceId: "1", name: "Alpha", macAddress: "", advertisedServices: []))
        reg.register(BluetoothEndpointDescriptor(deviceId: "2", name: "Mid", macAddress: "", advertisedServices: []))
        XCTAssertEqual(reg.allEndpoints.map { $0.name }, ["Alpha", "Mid", "Zeta"])
        XCTAssertEqual(reg.getEndpoint("2")?.name, "Mid")
    }

    func testRegistryStateDefaultsToDisconnected() {
        let reg = InMemoryBluetoothTransportRegistry()
        XCTAssertEqual(reg.state("unknown"), .disconnected)
        reg.setState("d", .connected)
        XCTAssertEqual(reg.state("d"), .connected)
    }

    func testRegistryAvgKbpsRead() {
        let reg = InMemoryBluetoothTransportRegistry()
        XCTAssertEqual(reg.avgKbpsRead("d"), 0.0, accuracy: 0.0001) // empty → 0
        reg.recordThroughput(BluetoothThroughputSample(deviceId: "d", kbpsRead: 100, kbpsWrite: 50, atUtc: Date()))
        reg.recordThroughput(BluetoothThroughputSample(deviceId: "d", kbpsRead: 300, kbpsWrite: 50, atUtc: Date()))
        reg.recordThroughput(BluetoothThroughputSample(deviceId: "other", kbpsRead: 999, kbpsWrite: 0, atUtc: Date()))
        XCTAssertEqual(reg.avgKbpsRead("d"), 200, accuracy: 0.0001)
    }

    // ── BluetoothNetworkTransport ────────────────────────────────────────────

    func testTransportKindAndAvailability() {
        let up = BluetoothNetworkTransport(adapter: LoopbackBleAdapter(available: true))
        XCTAssertEqual(up.kind, .bluetooth)
        XCTAssertTrue(up.isAvailable)

        let down = BluetoothNetworkTransport(adapter: LoopbackBleAdapter(available: false))
        XCTAssertFalse(down.isAvailable)
    }

    func testTransportStartHandsAdapterInboundAndSendLoopsBack() async throws {
        let adapter = LoopbackBleAdapter()
        let t = BluetoothNetworkTransport(adapter: adapter)
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
        let adapter = LoopbackBleAdapter()
        let t = BluetoothNetworkTransport(adapter: adapter)
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
        let t = BluetoothNetworkTransport(adapter: LoopbackBleAdapter())
        try await t.start()
        try await t.stop()
        // A receive() attached after stop should be an immediately-finished stream.
        let stream = t.receive()
        var count = 0
        for await _ in stream { count += 1 }
        XCTAssertEqual(count, 0)
    }
}
