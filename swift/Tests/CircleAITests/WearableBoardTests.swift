// WearableBoardTests.swift
//
// Exercises the Wearable records/enums Codable round-trips and the deterministic
// behaviour of InMemoryWearableBoard — devices (vendor-asc), sample recording
// (unknown-device throw), window reads (asc), latest value, and average (NaN
// when empty). Also round-trips the WearableContext biometric snapshot. Mirrors
// CircleAI.Wearable/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class WearableBoardTests: XCTestCase {

    func testEnumsCodableRoundTrip() throws {
        for k in WearableKind.allCases {
            XCTAssertEqual(try JSONDecoder().decode(WearableKind.self, from: try JSONEncoder().encode(k)), k)
        }
        for k in WearableTelemetryKind.allCases {
            XCTAssertEqual(try JSONDecoder().decode(WearableTelemetryKind.self, from: try JSONEncoder().encode(k)), k)
        }
        XCTAssertEqual(WearableKind.fitnessBand.rawValue, "FitnessBand")
        XCTAssertEqual(WearableTelemetryKind.skinTempC.rawValue, "SkinTempC")
    }

    func testWearableContextCodableRoundTrip() throws {
        let c = WearableContext(heartRateBpm: 72, stepCountToday: 4200, spO2Percent: 98, skinTempCelsius: 33.5, isWorkoutActive: false, capturedAt: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(WearableContext.self, from: try JSONEncoder().encode(c)), c)
        let sparse = WearableContext(heartRateBpm: nil, stepCountToday: nil, spO2Percent: nil, skinTempCelsius: nil, isWorkoutActive: true, capturedAt: Date(timeIntervalSince1970: 9))
        XCTAssertEqual(try JSONDecoder().decode(WearableContext.self, from: try JSONEncoder().encode(sparse)), sparse)
    }

    func testDevicesVendorOrderedAndRecordUnknownThrows() throws {
        let b = InMemoryWearableBoard()
        b.add(WearableDevice(deviceId: "d1", kind: .smartwatch, vendor: "Zephyr", firmwareVersion: "1.0", batteryPct: 80))
        b.add(WearableDevice(deviceId: "d2", kind: .fitnessBand, vendor: "Acme", firmwareVersion: "2.0", batteryPct: 50))
        XCTAssertEqual(b.devices.map { $0.deviceId }, ["d2", "d1"]) // Acme < Zephyr
        XCTAssertEqual(b.getDevice("d1")?.vendor, "Zephyr")
        XCTAssertThrowsError(try b.record(WearableSample(deviceId: "ghost", kind: .heartRate, value: 70, atUtc: Date(timeIntervalSince1970: 1)))) {
            XCTAssertEqual($0 as? WearableError, .unknownDevice("ghost"))
        }
    }

    func testReadSinceLatestAndAverage() throws {
        let b = InMemoryWearableBoard()
        b.add(WearableDevice(deviceId: "d1", kind: .smartwatch, vendor: "Z", firmwareVersion: "1", batteryPct: 100))
        let base = Date(timeIntervalSince1970: 1000)
        try b.record(WearableSample(deviceId: "d1", kind: .heartRate, value: 60, atUtc: base.addingTimeInterval(10)))
        try b.record(WearableSample(deviceId: "d1", kind: .heartRate, value: 80, atUtc: base.addingTimeInterval(30)))
        try b.record(WearableSample(deviceId: "d1", kind: .heartRate, value: 70, atUtc: base.addingTimeInterval(20)))
        try b.record(WearableSample(deviceId: "d1", kind: .heartRate, value: 999, atUtc: base.addingTimeInterval(-5))) // before window
        try b.record(WearableSample(deviceId: "d1", kind: .steps, value: 5, atUtc: base.addingTimeInterval(40)))
        XCTAssertEqual(b.readSince(deviceId: "d1", kind: .heartRate, since: base).map { $0.value }, [60, 70, 80])
        XCTAssertEqual(b.latestValue(deviceId: "d1", kind: .heartRate), 80) // atUtc=30 newest
        XCTAssertNil(b.latestValue(deviceId: "d1", kind: .stress))
        XCTAssertEqual(b.averageValue(deviceId: "d1", kind: .heartRate, since: base), 70, accuracy: 1e-9) // (60+70+80)/3
        XCTAssertTrue(b.averageValue(deviceId: "d1", kind: .oxygenPct, since: base).isNaN) // empty -> NaN
    }
}
