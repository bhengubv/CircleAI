// PetsBoardTests.swift
//
// Exercises the Pets records' Codable round-trips and the deterministic
// behaviour of InMemoryPetsBoard — pets (name-ordered), vaccinations (per-pet,
// newest-first), weight history (per-pet, oldest-first), and vet appointments
// (upcoming at/after an injected `now`, ascending). Also checks the
// PetsDomainContext constants. Mirrors CircleAI.Pets/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class PetsBoardTests: XCTestCase {

    private func pet(_ id: String, _ name: String) -> Pet {
        Pet(petId: id, name: name, species: "dog", breed: nil, dateOfBirth: Date(timeIntervalSince1970: 0))
    }

    func testVaccinationCodableRoundTrip() throws {
        let v = Vaccination(petId: "p1", vaccine: "Rabies", administeredUtc: Date(timeIntervalSince1970: 5), boosterDueUtc: Date(timeIntervalSince1970: 9))
        XCTAssertEqual(try JSONDecoder().decode(Vaccination.self, from: try JSONEncoder().encode(v)), v)
    }

    func testVetAppointmentCodableRoundTrip() throws {
        let a = VetAppointment(apptId: "a1", petId: "p1", reason: "checkup", atUtc: Date(timeIntervalSince1970: 7), vet: "Dr Vet")
        XCTAssertEqual(try JSONDecoder().decode(VetAppointment.self, from: try JSONEncoder().encode(a)), a)
    }

    func testPetsNameOrdered() {
        let b = InMemoryPetsBoard()
        b.add(pet("p2", "Zephyr"))
        b.add(pet("p1", "Ace"))
        XCTAssertEqual(b.getPet("p1")?.name, "Ace")
        XCTAssertEqual(b.pets.map { $0.name }, ["Ace", "Zephyr"])
    }

    func testVaccinationsNewestFirstPerPet() {
        let b = InMemoryPetsBoard()
        b.recordVaccination(Vaccination(petId: "p1", vaccine: "A", administeredUtc: Date(timeIntervalSince1970: 1), boosterDueUtc: nil))
        b.recordVaccination(Vaccination(petId: "p1", vaccine: "B", administeredUtc: Date(timeIntervalSince1970: 3), boosterDueUtc: nil))
        b.recordVaccination(Vaccination(petId: "p2", vaccine: "C", administeredUtc: Date(timeIntervalSince1970: 2), boosterDueUtc: nil))
        XCTAssertEqual(b.vaccinationsFor("p1").map { $0.vaccine }, ["B", "A"])
        XCTAssertEqual(b.vaccinationsFor("p2").map { $0.vaccine }, ["C"])
    }

    func testWeightHistoryOldestFirstPerPet() {
        let b = InMemoryPetsBoard()
        b.recordWeight(WeightSample(petId: "p1", weightKg: 10, atUtc: Date(timeIntervalSince1970: 3)))
        b.recordWeight(WeightSample(petId: "p1", weightKg: 9, atUtc: Date(timeIntervalSince1970: 1)))
        b.recordWeight(WeightSample(petId: "p1", weightKg: 11, atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.weightHistory("p1").map { $0.weightKg }, [9, 11, 10])
    }

    func testUpcomingAppointmentsAtOrAfterNowAscending() {
        let b = InMemoryPetsBoard()
        let now = Date(timeIntervalSince1970: 1000)
        b.schedule(VetAppointment(apptId: "a1", petId: "p1", reason: "r", atUtc: now.addingTimeInterval(30), vet: "v"))
        b.schedule(VetAppointment(apptId: "a2", petId: "p1", reason: "r", atUtc: now.addingTimeInterval(10), vet: "v"))
        b.schedule(VetAppointment(apptId: "past", petId: "p1", reason: "r", atUtc: now.addingTimeInterval(-10), vet: "v"))
        b.schedule(VetAppointment(apptId: "now", petId: "p1", reason: "r", atUtc: now, vet: "v"))   // boundary included
        XCTAssertEqual(b.upcomingAppointments(now: now).map { $0.apptId }, ["now", "a2", "a1"])
    }

    func testDomainContext() {
        XCTAssertTrue(PetsDomainContext.systemPromptSnippet.contains("[DOMAIN: Pets]"))
        XCTAssertEqual(PetsDomainContext.complianceFlags, ["Animals_Protection_Act_71_1962", "POPIA", "Vet_Referral_Required"])
        XCTAssertEqual(PetsDomainContext.suggestedTools, ["vet_finder", "pet_health_db", "training_tools", "calendar"])
    }
}
