// SafetyChildBoardTests.swift
//
// Exercises the child-safety records' Codable round-trips and the deterministic
// behaviour of InMemoryChildSafetyBoard — trusted-adult ring ordering, geofence
// definition/lookup, the Haversine inside/outside fence test, and check-in
// history limits + ordering. Mirrors the C# reference in
// CircleAI.Safety.Child/ChildSafetyPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class SafetyChildBoardTests: XCTestCase {

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testTrustedAdultCodableRoundTrip() throws {
        let a = TrustedAdult(adultId: "a1", name: "Mum", phone: "555", relationship: "parent", ringPriority: 1)
        let back = try JSONDecoder().decode(TrustedAdult.self, from: try JSONEncoder().encode(a))
        XCTAssertEqual(back, a)
    }

    func testGeofenceCodableRoundTrip() throws {
        let g = Geofence(fenceId: "g1", name: "home", centreLat: -26.2, centreLon: 28.0, radiusMeters: 150)
        let back = try JSONDecoder().decode(Geofence.self, from: try JSONEncoder().encode(g))
        XCTAssertEqual(back, g)
    }

    func testCheckInCodableRoundTrip() throws {
        let c = CheckIn(childId: "kid", status: "safe", lat: 1.0, lon: 2.0,
                        atUtc: Date(timeIntervalSince1970: 42))
        let back = try JSONDecoder().decode(CheckIn.self, from: try JSONEncoder().encode(c))
        XCTAssertEqual(back, c)
    }

    // ── Trusted-adult ring ───────────────────────────────────────────────────

    func testRingOrderedByPriority() {
        let board = InMemoryChildSafetyBoard()
        board.addAdult(TrustedAdult(adultId: "c", name: "C", phone: "3", relationship: "r", ringPriority: 3))
        board.addAdult(TrustedAdult(adultId: "a", name: "A", phone: "1", relationship: "r", ringPriority: 1))
        board.addAdult(TrustedAdult(adultId: "b", name: "B", phone: "2", relationship: "r", ringPriority: 2))
        XCTAssertEqual(board.ringOrdered.map { $0.adultId }, ["a", "b", "c"])
    }

    func testAddAdultReplacesById() {
        let board = InMemoryChildSafetyBoard()
        board.addAdult(TrustedAdult(adultId: "a1", name: "Old", phone: "1", relationship: "r", ringPriority: 5))
        board.addAdult(TrustedAdult(adultId: "a1", name: "New", phone: "1", relationship: "r", ringPriority: 1))
        XCTAssertEqual(board.ringOrdered.count, 1)
        XCTAssertEqual(board.ringOrdered.first?.name, "New")
        XCTAssertEqual(board.ringOrdered.first?.ringPriority, 1)
    }

    // ── Geofences ────────────────────────────────────────────────────────────

    func testDefineAndGetGeofence() {
        let board = InMemoryChildSafetyBoard()
        board.defineGeofence(Geofence(fenceId: "g1", name: "school", centreLat: 1, centreLon: 2, radiusMeters: 100))
        XCTAssertEqual(board.getGeofence("g1")?.name, "school")
        XCTAssertNil(board.getGeofence("missing"))
    }

    func testDefineGeofenceReplacesById() {
        let board = InMemoryChildSafetyBoard()
        board.defineGeofence(Geofence(fenceId: "g1", name: "old", centreLat: 0, centreLon: 0, radiusMeters: 10))
        board.defineGeofence(Geofence(fenceId: "g1", name: "new", centreLat: 0, centreLon: 0, radiusMeters: 20))
        XCTAssertEqual(board.getGeofence("g1")?.name, "new")
        XCTAssertEqual(board.getGeofence("g1")?.radiusMeters, 20)
    }

    // ── Haversine fence test ─────────────────────────────────────────────────
    //
    // Fence at (0,0), radius 50 km. 1° of latitude ≈ 111.195 km, so:
    //   (0.1, 0) ≈ 11.12 km  → inside
    //   (1.0, 0) ≈ 111.19 km → outside

    func testIsInsideAnyFenceTrueForNearPoint() {
        let board = InMemoryChildSafetyBoard()
        board.defineGeofence(Geofence(fenceId: "g", name: "n", centreLat: 0, centreLon: 0, radiusMeters: 50_000))
        XCTAssertTrue(board.isInsideAnyFence(lat: 0.1, lon: 0.0))
        // Exact centre is trivially inside.
        XCTAssertTrue(board.isInsideAnyFence(lat: 0.0, lon: 0.0))
    }

    func testIsInsideAnyFenceFalseForFarPoint() {
        let board = InMemoryChildSafetyBoard()
        board.defineGeofence(Geofence(fenceId: "g", name: "n", centreLat: 0, centreLon: 0, radiusMeters: 50_000))
        XCTAssertFalse(board.isInsideAnyFence(lat: 1.0, lon: 0.0))
    }

    func testIsInsideAnyFenceFalseWhenNoFences() {
        XCTAssertFalse(InMemoryChildSafetyBoard().isInsideAnyFence(lat: 0, lon: 0))
    }

    func testIsInsideAnyFenceMatchesAnyOfSeveral() {
        let board = InMemoryChildSafetyBoard()
        board.defineGeofence(Geofence(fenceId: "far", name: "far", centreLat: 40, centreLon: 40, radiusMeters: 100))
        board.defineGeofence(Geofence(fenceId: "near", name: "near", centreLat: 0, centreLon: 0, radiusMeters: 50_000))
        XCTAssertTrue(board.isInsideAnyFence(lat: 0.05, lon: 0.0)) // inside "near" only
    }

    // ── Check-ins ────────────────────────────────────────────────────────────

    func testRecentCheckInsFilterByChildAndOrderNewestFirst() throws {
        let board = InMemoryChildSafetyBoard()
        board.recordCheckIn(CheckIn(childId: "kid", status: "a", lat: nil, lon: nil, atUtc: Date(timeIntervalSince1970: 100)))
        board.recordCheckIn(CheckIn(childId: "other", status: "x", lat: nil, lon: nil, atUtc: Date(timeIntervalSince1970: 250)))
        board.recordCheckIn(CheckIn(childId: "kid", status: "c", lat: nil, lon: nil, atUtc: Date(timeIntervalSince1970: 300)))
        board.recordCheckIn(CheckIn(childId: "kid", status: "b", lat: nil, lon: nil, atUtc: Date(timeIntervalSince1970: 200)))

        let recent = try board.recentCheckIns(childId: "kid")
        XCTAssertEqual(recent.map { $0.status }, ["c", "b", "a"]) // newest-first, "other" excluded
    }

    func testRecentCheckInsHonoursLimit() throws {
        let board = InMemoryChildSafetyBoard()
        for i in 0..<10 {
            board.recordCheckIn(CheckIn(childId: "kid", status: "s\(i)", lat: nil, lon: nil,
                                        atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        let recent = try board.recentCheckIns(childId: "kid", limit: 3)
        XCTAssertEqual(recent.count, 3)
        XCTAssertEqual(recent.map { $0.status }, ["s9", "s8", "s7"]) // three newest
    }

    func testRecentCheckInsDefaultLimitIs20() throws {
        let board = InMemoryChildSafetyBoard()
        for i in 0..<25 {
            board.recordCheckIn(CheckIn(childId: "kid", status: "s\(i)", lat: nil, lon: nil,
                                        atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        let recent = try board.recentCheckIns(childId: "kid")
        XCTAssertEqual(recent.count, 20)
    }

    func testRecentCheckInsRejectsNonPositiveLimit() {
        let board = InMemoryChildSafetyBoard()
        board.recordCheckIn(CheckIn(childId: "kid", status: "a", lat: nil, lon: nil, atUtc: Date()))
        XCTAssertThrowsError(try board.recentCheckIns(childId: "kid", limit: 0)) { error in
            XCTAssertEqual(error as? ChildSafetyError, .limitOutOfRange)
        }
        XCTAssertThrowsError(try board.recentCheckIns(childId: "kid", limit: -5)) { error in
            XCTAssertEqual(error as? ChildSafetyError, .limitOutOfRange)
        }
    }

    func testRecentCheckInsEmptyForUnknownChild() throws {
        let board = InMemoryChildSafetyBoard()
        XCTAssertTrue(try board.recentCheckIns(childId: "nobody").isEmpty)
    }
}
