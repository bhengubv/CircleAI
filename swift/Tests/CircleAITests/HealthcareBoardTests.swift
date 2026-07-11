// HealthcareBoardTests.swift
//
// Exercises the healthcare records' Codable round-trips and the deterministic
// behaviour of InMemoryHealthcareBoard — patient registration, appointment
// scheduling + status updates (incl. the unknown-appointment throw), and
// prescription ordering. Mirrors CircleAI.Healthcare/HealthcarePrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class HealthcareBoardTests: XCTestCase {

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testPatientCodableRoundTrip() throws {
        let p = Patient(patientId: "p1", name: "Ada", dateOfBirth: Date(timeIntervalSince1970: 1000))
        XCTAssertEqual(try JSONDecoder().decode(Patient.self, from: try JSONEncoder().encode(p)), p)
    }

    func testAppointmentCodableRoundTrip() throws {
        let a = HealthAppointment(apptId: "a1", patientId: "p1", provider: "Dr X",
                                  atUtc: Date(timeIntervalSince1970: 2000), status: "booked")
        XCTAssertEqual(try JSONDecoder().decode(HealthAppointment.self, from: try JSONEncoder().encode(a)), a)
    }

    func testPrescriptionCodableRoundTrip() throws {
        let r = Prescription(rxId: "r1", patientId: "p1", medicationName: "Amoxicillin",
                             dose: "500mg", frequency: "TID", prescribedUtc: Date(timeIntervalSince1970: 3000))
        XCTAssertEqual(try JSONDecoder().decode(Prescription.self, from: try JSONEncoder().encode(r)), r)
    }

    // ── Patients ─────────────────────────────────────────────────────────────

    func testRegisterAndGetPatient() {
        let b = InMemoryHealthcareBoard()
        b.register(Patient(patientId: "p1", name: "Ada", dateOfBirth: Date(timeIntervalSince1970: 0)))
        XCTAssertEqual(b.getPatient("p1")?.name, "Ada")
        XCTAssertNil(b.getPatient("missing"))
    }

    func testRegisterReplacesById() {
        let b = InMemoryHealthcareBoard()
        b.register(Patient(patientId: "p1", name: "Old", dateOfBirth: Date(timeIntervalSince1970: 0)))
        b.register(Patient(patientId: "p1", name: "New", dateOfBirth: Date(timeIntervalSince1970: 0)))
        XCTAssertEqual(b.getPatient("p1")?.name, "New")
    }

    // ── Appointments ─────────────────────────────────────────────────────────

    func testAppointmentsForOrderedAscendingByTime() {
        let b = InMemoryHealthcareBoard()
        b.schedule(HealthAppointment(apptId: "a3", patientId: "p1", provider: "X",
                                     atUtc: Date(timeIntervalSince1970: 300), status: "booked"))
        b.schedule(HealthAppointment(apptId: "a1", patientId: "p1", provider: "X",
                                     atUtc: Date(timeIntervalSince1970: 100), status: "booked"))
        b.schedule(HealthAppointment(apptId: "a2", patientId: "other", provider: "X",
                                     atUtc: Date(timeIntervalSince1970: 200), status: "booked"))
        XCTAssertEqual(b.appointmentsFor(patientId: "p1").map { $0.apptId }, ["a1", "a3"])
    }

    func testUpdateStatusMutatesAppointment() throws {
        let b = InMemoryHealthcareBoard()
        b.schedule(HealthAppointment(apptId: "a1", patientId: "p1", provider: "X",
                                     atUtc: Date(timeIntervalSince1970: 100), status: "booked"))
        try b.updateStatus(apptId: "a1", status: "seen")
        XCTAssertEqual(b.appointmentsFor(patientId: "p1").first?.status, "seen")
    }

    func testUpdateStatusThrowsForUnknownAppointment() {
        let b = InMemoryHealthcareBoard()
        XCTAssertThrowsError(try b.updateStatus(apptId: "nope", status: "x")) { error in
            XCTAssertEqual(error as? HealthcareError, .unknownAppointment("nope"))
        }
    }

    // ── Prescriptions ────────────────────────────────────────────────────────

    func testPrescriptionsForOrderedNewestFirst() {
        let b = InMemoryHealthcareBoard()
        b.prescribe(Prescription(rxId: "r1", patientId: "p1", medicationName: "A", dose: "1", frequency: "d",
                                 prescribedUtc: Date(timeIntervalSince1970: 100)))
        b.prescribe(Prescription(rxId: "r2", patientId: "p1", medicationName: "B", dose: "1", frequency: "d",
                                 prescribedUtc: Date(timeIntervalSince1970: 300)))
        b.prescribe(Prescription(rxId: "r3", patientId: "other", medicationName: "C", dose: "1", frequency: "d",
                                 prescribedUtc: Date(timeIntervalSince1970: 400)))
        XCTAssertEqual(b.prescriptionsFor(patientId: "p1").map { $0.rxId }, ["r2", "r1"])
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(HealthcareDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Healthcare]"))
        XCTAssertTrue(HealthcareDomainContext.complianceFlags.contains("HIPAA"))
        XCTAssertEqual(HealthcareDomainContext.suggestedTools.first, "ehr_system")
    }
}
