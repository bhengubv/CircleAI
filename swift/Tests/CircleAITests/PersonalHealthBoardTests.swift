// PersonalHealthBoardTests.swift
//
// Exercises the personal-health records' Codable round-trips (incl. the
// Int-backed VitalKind ordinals) and the deterministic behaviour of
// InMemoryPersonalHealthBoard — vitals record / read-since / latest, allergies,
// and medications with active filtering + end (incl. unknown-med throw).
// Mirrors CircleAI.Personal.Health/PersonalHealthPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class PersonalHealthBoardTests: XCTestCase {

    // ── Enum ordinals ────────────────────────────────────────────────────────

    func testVitalKindOrdinalsMatchCSharp() {
        XCTAssertEqual(VitalKind.bloodPressureSystolic.rawValue, 0)
        XCTAssertEqual(VitalKind.bloodPressureDiastolic.rawValue, 1)
        XCTAssertEqual(VitalKind.glucoseMgDl.rawValue, 2)
        XCTAssertEqual(VitalKind.weightKg.rawValue, 3)
        XCTAssertEqual(VitalKind.heartRateBpm.rawValue, 4)
        XCTAssertEqual(VitalKind.temperatureC.rawValue, 5)
        XCTAssertEqual(VitalKind.oxygenPct.rawValue, 6)
        XCTAssertEqual(VitalKind.stepsCount.rawValue, 7)
        XCTAssertEqual(VitalKind.allCases.count, 8)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testVitalReadingCodableRoundTrip() throws {
        let v = VitalReading(kind: .glucoseMgDl, value: 5.4, atUtc: Date(timeIntervalSince1970: 10), note: "fasting")
        XCTAssertEqual(try JSONDecoder().decode(VitalReading.self, from: try JSONEncoder().encode(v)), v)
    }

    func testAllergyAndMedicationCodableRoundTrip() throws {
        let a = Allergy(allergyId: "a1", substance: "penicillin", severity: "severe")
        XCTAssertEqual(try JSONDecoder().decode(Allergy.self, from: try JSONEncoder().encode(a)), a)
        let m = Medication(medId: "m1", name: "Metformin", dose: "500mg", frequency: "BD",
                           startedAtUtc: Date(timeIntervalSince1970: 1), endedAtUtc: nil)
        XCTAssertEqual(try JSONDecoder().decode(Medication.self, from: try JSONEncoder().encode(m)), m)
    }

    // ── Vitals ───────────────────────────────────────────────────────────────

    func testReadSinceFiltersByKindAndTimeAscending() {
        let b = InMemoryPersonalHealthBoard()
        b.record(VitalReading(kind: .weightKg, value: 80, atUtc: Date(timeIntervalSince1970: 100), note: nil))
        b.record(VitalReading(kind: .weightKg, value: 79, atUtc: Date(timeIntervalSince1970: 300), note: nil))
        b.record(VitalReading(kind: .weightKg, value: 81, atUtc: Date(timeIntervalSince1970: 50), note: nil)) // before cutoff
        b.record(VitalReading(kind: .glucoseMgDl, value: 5, atUtc: Date(timeIntervalSince1970: 400), note: nil)) // wrong kind
        let res = b.readSince(kind: .weightKg, since: Date(timeIntervalSince1970: 100))
        XCTAssertEqual(res.map { $0.value }, [80, 79]) // ascending, cutoff + kind applied
    }

    func testLatestReturnsNewestOfKind() {
        let b = InMemoryPersonalHealthBoard()
        b.record(VitalReading(kind: .heartRateBpm, value: 60, atUtc: Date(timeIntervalSince1970: 100), note: nil))
        b.record(VitalReading(kind: .heartRateBpm, value: 72, atUtc: Date(timeIntervalSince1970: 500), note: nil))
        XCTAssertEqual(b.latest(kind: .heartRateBpm)?.value, 72)
        XCTAssertNil(b.latest(kind: .oxygenPct))
    }

    // ── Allergies ────────────────────────────────────────────────────────────

    func testAddAllergyReplacesById() {
        let b = InMemoryPersonalHealthBoard()
        b.addAllergy(Allergy(allergyId: "a1", substance: "old", severity: "mild"))
        b.addAllergy(Allergy(allergyId: "a1", substance: "new", severity: "severe"))
        XCTAssertEqual(b.allergies.count, 1)
        XCTAssertEqual(b.allergies.first?.substance, "new")
    }

    // ── Medications ──────────────────────────────────────────────────────────

    func testActiveMedicationsExcludesEndedAndOrdersByName() throws {
        let b = InMemoryPersonalHealthBoard()
        b.addMedication(Medication(medId: "m1", name: "Zestril", dose: "1", frequency: "d",
                                   startedAtUtc: Date(timeIntervalSince1970: 1), endedAtUtc: nil))
        b.addMedication(Medication(medId: "m2", name: "Aspirin", dose: "1", frequency: "d",
                                   startedAtUtc: Date(timeIntervalSince1970: 1), endedAtUtc: nil))
        b.addMedication(Medication(medId: "m3", name: "Old", dose: "1", frequency: "d",
                                   startedAtUtc: Date(timeIntervalSince1970: 1),
                                   endedAtUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.activeMedications().map { $0.name }, ["Aspirin", "Zestril"])
    }

    func testEndMedicationSetsEndDateAndThrowsForUnknown() throws {
        let b = InMemoryPersonalHealthBoard()
        b.addMedication(Medication(medId: "m1", name: "Metformin", dose: "1", frequency: "d",
                                   startedAtUtc: Date(timeIntervalSince1970: 1), endedAtUtc: nil))
        try b.endMedication(medId: "m1", endedAtUtc: Date(timeIntervalSince1970: 999))
        XCTAssertTrue(b.activeMedications().isEmpty)

        XCTAssertThrowsError(try b.endMedication(medId: "ghost", endedAtUtc: Date())) { error in
            XCTAssertEqual(error as? PersonalHealthError, .unknownMedication("ghost"))
        }
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(PersonalHealthDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Personal.Health]"))
        XCTAssertTrue(PersonalHealthDomainContext.complianceFlags.contains("Not_Medical_Advice"))
    }
}
