// AmbientBoardTests.swift
//
// Exercises the Ambient records' Codable round-trips and the deterministic
// behaviour of InMemoryAmbientBoard — readings (latest, history desc/limited),
// preferences, and the comfort check (temp/humidity/noise thresholds). Mirrors
// CircleAI.Ambient/AmbientPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class AmbientBoardTests: XCTestCase {

    func testAmbientReadingCodableRoundTrip() throws {
        let r = AmbientReading(deviceId: "d1", temperatureC: 22, humidity: 45, luxLight: 300, dbNoise: 40, atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(AmbientReading.self, from: try JSONEncoder().encode(r)), r)
    }

    func testLatestAndHistoryDescendingLimited() {
        let b = InMemoryAmbientBoard()
        b.record(AmbientReading(deviceId: "d1", temperatureC: 20, humidity: 40, luxLight: 1, dbNoise: 1, atUtc: Date(timeIntervalSince1970: 10)))
        b.record(AmbientReading(deviceId: "d1", temperatureC: 21, humidity: 41, luxLight: 1, dbNoise: 1, atUtc: Date(timeIntervalSince1970: 30)))
        b.record(AmbientReading(deviceId: "d1", temperatureC: 22, humidity: 42, luxLight: 1, dbNoise: 1, atUtc: Date(timeIntervalSince1970: 20)))
        b.record(AmbientReading(deviceId: "d2", temperatureC: 99, humidity: 99, luxLight: 1, dbNoise: 1, atUtc: Date(timeIntervalSince1970: 99)))
        XCTAssertEqual(b.latest(deviceId: "d1")?.temperatureC, 21) // atUtc=30 is newest
        XCTAssertEqual(b.history(deviceId: "d1").map { $0.atUtc.timeIntervalSince1970 }, [30, 20, 10])
        XCTAssertEqual(b.history(deviceId: "d1", limit: 1).map { $0.atUtc.timeIntervalSince1970 }, [30])
        XCTAssertNil(b.latest(deviceId: "none"))
    }

    func testIsComfortable() {
        let b = InMemoryAmbientBoard()
        b.setPreference(AmbientPreference(location: "office", targetTempC: 22, targetHumidity: 45, maxNoiseDb: 50))
        // No reading yet -> not comfortable.
        XCTAssertFalse(b.isComfortable(deviceId: "d1", location: "office"))
        b.record(AmbientReading(deviceId: "d1", temperatureC: 23, humidity: 50, luxLight: 1, dbNoise: 45, atUtc: Date(timeIntervalSince1970: 1)))
        // |23-22|<=2, |50-45|<=10, 45<=50 -> comfortable.
        XCTAssertTrue(b.isComfortable(deviceId: "d1", location: "office"))
        // Too noisy.
        b.record(AmbientReading(deviceId: "d1", temperatureC: 22, humidity: 45, luxLight: 1, dbNoise: 60, atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertFalse(b.isComfortable(deviceId: "d1", location: "office"))
        // Missing preference.
        XCTAssertFalse(b.isComfortable(deviceId: "d1", location: "nowhere"))
    }
}
