// IoTBoardTests.swift
//
// Exercises the IoT records' Codable round-trips and the deterministic
// behaviour of InMemoryIoTBoard — device registry (name-ordered), telemetry
// (record, latest-value incl. NaN, newest-first history with limit + throw),
// and commands (send, newest-first per device). Mirrors CircleAI.IoT/IoTPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class IoTBoardTests: XCTestCase {

    private func device(_ id: String, _ name: String) -> IoTDevice {
        IoTDevice(deviceId: id, name: name, kind: "sensor", firmwareVersion: "1.0", lastSeenUtc: Date(timeIntervalSince1970: 0))
    }

    func testTelemetryCodableRoundTrip() throws {
        let t = IoTTelemetry(deviceId: "d1", metric: "temp", value: 21.5, atUtc: Date(timeIntervalSince1970: 3))
        XCTAssertEqual(try JSONDecoder().decode(IoTTelemetry.self, from: try JSONEncoder().encode(t)), t)
    }

    func testCommandCodableRoundTrip() throws {
        let c = IoTCommand(commandId: "c1", deviceId: "d1", action: "on", argumentsJson: "{}", sentUtc: Date(timeIntervalSince1970: 4))
        XCTAssertEqual(try JSONDecoder().decode(IoTCommand.self, from: try JSONEncoder().encode(c)), c)
    }

    func testRegisterGetAndDevicesNameOrdered() {
        let b = InMemoryIoTBoard()
        b.register(device("d2", "Zeta"))
        b.register(device("d1", "Alpha"))
        XCTAssertEqual(b.getDevice("d1")?.name, "Alpha")
        XCTAssertNil(b.getDevice("nope"))
        XCTAssertEqual(b.devices.map { $0.name }, ["Alpha", "Zeta"])
    }

    func testLatestValueIsMostRecentOrNaN() {
        let b = InMemoryIoTBoard()
        XCTAssertTrue(b.latestValue(deviceId: "d1", metric: "temp").isNaN)
        b.recordTelemetry(IoTTelemetry(deviceId: "d1", metric: "temp", value: 20, atUtc: Date(timeIntervalSince1970: 1)))
        b.recordTelemetry(IoTTelemetry(deviceId: "d1", metric: "temp", value: 25, atUtc: Date(timeIntervalSince1970: 3)))
        b.recordTelemetry(IoTTelemetry(deviceId: "d1", metric: "temp", value: 22, atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.latestValue(deviceId: "d1", metric: "temp"), 25)
        XCTAssertTrue(b.latestValue(deviceId: "d1", metric: "humidity").isNaN)
    }

    func testHistoryNewestFirstWithLimitAndThrow() throws {
        let b = InMemoryIoTBoard()
        for i in 0..<4 {
            b.recordTelemetry(IoTTelemetry(deviceId: "d1", metric: "temp", value: Double(i), atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        b.recordTelemetry(IoTTelemetry(deviceId: "d2", metric: "temp", value: 99, atUtc: Date(timeIntervalSince1970: 9)))
        let recent = try b.history(deviceId: "d1", metric: "temp", limit: 2)
        XCTAssertEqual(recent.map { $0.value }, [3, 2])
        XCTAssertEqual(try b.history(deviceId: "d1", metric: "temp").count, 4)   // default limit
        XCTAssertThrowsError(try b.history(deviceId: "d1", metric: "temp", limit: 0)) { XCTAssertEqual($0 as? IoTError, .limitOutOfRange) }
    }

    func testCommandsForNewestFirst() {
        let b = InMemoryIoTBoard()
        b.sendCommand(IoTCommand(commandId: "c1", deviceId: "d1", action: "on", argumentsJson: "{}", sentUtc: Date(timeIntervalSince1970: 1)))
        b.sendCommand(IoTCommand(commandId: "c2", deviceId: "d1", action: "off", argumentsJson: "{}", sentUtc: Date(timeIntervalSince1970: 3)))
        b.sendCommand(IoTCommand(commandId: "c3", deviceId: "d2", action: "on", argumentsJson: "{}", sentUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.commandsFor(deviceId: "d1").map { $0.commandId }, ["c2", "c1"])
        XCTAssertEqual(b.commandsFor(deviceId: "d2").map { $0.commandId }, ["c3"])
    }
}
