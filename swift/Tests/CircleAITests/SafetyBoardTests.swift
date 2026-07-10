// SafetyBoardTests.swift
//
// Locks the IncidentSeverity wire ordinals + ordering, the Codable round-trips
// of the safety records, and the deterministic behaviour of InMemorySafetyBoard.
// Mirrors the C# reference in CircleAI.Safety/SafetyPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class SafetyBoardTests: XCTestCase {

    private func incident(_ id: String, _ sev: IncidentSeverity, at seconds: TimeInterval) -> Incident {
        Incident(incidentId: id, severity: sev, description: id,
                 latitude: nil, longitude: nil, atUtc: Date(timeIntervalSince1970: seconds))
    }

    // ── IncidentSeverity ─────────────────────────────────────────────────────

    func testIncidentSeverityOrdinals() {
        XCTAssertEqual(IncidentSeverity.info.rawValue,      0)
        XCTAssertEqual(IncidentSeverity.warning.rawValue,   1)
        XCTAssertEqual(IncidentSeverity.critical.rawValue,  2)
        XCTAssertEqual(IncidentSeverity.emergency.rawValue, 3)
        XCTAssertEqual(IncidentSeverity.allCases.count,     4)
    }

    func testIncidentSeverityIsComparable() {
        XCTAssertTrue(IncidentSeverity.info < IncidentSeverity.warning)
        XCTAssertTrue(IncidentSeverity.critical < IncidentSeverity.emergency)
        XCTAssertEqual([IncidentSeverity.warning, .emergency, .info].max(), .emergency)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testIncidentCodableRoundTrip() throws {
        let i = Incident(incidentId: "i1", severity: .critical, description: "fire",
                         latitude: -26.2041, longitude: 28.0473,
                         atUtc: Date(timeIntervalSince1970: 1_700_000_000))
        let back = try JSONDecoder().decode(Incident.self, from: try JSONEncoder().encode(i))
        XCTAssertEqual(back, i)
    }

    func testHazardCodableRoundTrip() throws {
        let h = Hazard(hazardId: "h1", description: "wet floor", category: "slip",
                       notedUtc: Date(timeIntervalSince1970: 5))
        let back = try JSONDecoder().decode(Hazard.self, from: try JSONEncoder().encode(h))
        XCTAssertEqual(back, h)
    }

    func testEmergencyContactCodableRoundTrip() throws {
        let c = EmergencyContact(contactId: "c1", name: "Alex", phone: "10111", relationship: "neighbour")
        let back = try JSONDecoder().decode(EmergencyContact.self, from: try JSONEncoder().encode(c))
        XCTAssertEqual(back, c)
    }

    // ── Incident log + ordering ──────────────────────────────────────────────

    func testActiveReturnsNewestFirst() {
        let board = InMemorySafetyBoard()
        board.log(incident("old", .info, at: 100))
        board.log(incident("new", .info, at: 300))
        board.log(incident("mid", .info, at: 200))
        let ids = board.active.map { $0.incidentId }
        XCTAssertEqual(ids, ["new", "mid", "old"])
    }

    func testActiveIsEmptyInitially() {
        XCTAssertTrue(InMemorySafetyBoard().active.isEmpty)
    }

    // ── Severity filtering ───────────────────────────────────────────────────

    func testAtOrAboveSeverityFiltersAndOrders() {
        let board = InMemorySafetyBoard()
        board.log(incident("info", .info, at: 100))
        board.log(incident("warn", .warning, at: 200))
        board.log(incident("crit", .critical, at: 300))
        board.log(incident("emerg", .emergency, at: 400))

        let atCritical = board.atOrAboveSeverity(.critical).map { $0.incidentId }
        XCTAssertEqual(atCritical, ["emerg", "crit"]) // newest-first, >= critical

        let atInfo = board.atOrAboveSeverity(.info).map { $0.incidentId }
        XCTAssertEqual(atInfo, ["emerg", "crit", "warn", "info"])

        let atEmergency = board.atOrAboveSeverity(.emergency).map { $0.incidentId }
        XCTAssertEqual(atEmergency, ["emerg"])
    }

    // ── Hazards ──────────────────────────────────────────────────────────────

    func testNoteHazardReplacesById() {
        let board = InMemorySafetyBoard()
        board.noteHazard(Hazard(hazardId: "h1", description: "first", category: "x", notedUtc: Date(timeIntervalSince1970: 10)))
        board.noteHazard(Hazard(hazardId: "h1", description: "second", category: "x", notedUtc: Date(timeIntervalSince1970: 20)))
        XCTAssertEqual(board.hazards.count, 1)
        XCTAssertEqual(board.hazards.first?.description, "second")
    }

    func testHazardsReturnedNewestFirst() {
        let board = InMemorySafetyBoard()
        board.noteHazard(Hazard(hazardId: "a", description: "a", category: "x", notedUtc: Date(timeIntervalSince1970: 100)))
        board.noteHazard(Hazard(hazardId: "b", description: "b", category: "x", notedUtc: Date(timeIntervalSince1970: 300)))
        board.noteHazard(Hazard(hazardId: "c", description: "c", category: "x", notedUtc: Date(timeIntervalSince1970: 200)))
        XCTAssertEqual(board.hazards.map { $0.hazardId }, ["b", "c", "a"])
    }

    // ── Contacts ─────────────────────────────────────────────────────────────

    func testContactsPreserveInsertionOrderAndFirst() {
        let board = InMemorySafetyBoard()
        XCTAssertNil(board.firstContact)
        board.addContact(EmergencyContact(contactId: "c1", name: "First", phone: "1", relationship: "r"))
        board.addContact(EmergencyContact(contactId: "c2", name: "Second", phone: "2", relationship: "r"))
        XCTAssertEqual(board.contacts.map { $0.contactId }, ["c1", "c2"])
        XCTAssertEqual(board.firstContact?.contactId, "c1")
    }
}
