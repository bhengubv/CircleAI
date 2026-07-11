// Pets.swift
//
// Port of the Pets vertical from src/CircleAI.Pets/PetsPrimitives.cs and the
// static domain-context constants from PetsDomainContext.cs:
//   • Pet, Vaccination, WeightSample, VetAppointment — domain records
//   • IPetsBoard             — pets, vaccinations, weight history, vet appts
//   • InMemoryPetsBoard      — deterministic in-memory impl
//   • PetsDomainContext      — system-prompt snippet + flags
//
// The Companion-facing wrapper (PetsCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `DateTime`/`DateTimeOffset` → `Date`.
//   • `Pets` is ordered ascending by Name.
//   • `VaccinationsFor` returns the pet's vaccinations newest-first (by
//     AdministeredUtc). `WeightHistory` returns the pet's samples oldest-first
//     (ascending by AtUtc).
//   • `UpcomingAppointments` returns appointments at/after "now", ordered
//     ascending by AtUtc. The C# impl uses `DateTimeOffset.UtcNow`; here a
//     `now` parameter is injected (defaulting to `Date()`) so the deterministic
//     behaviour is testable — matching the "inject external deps" rule.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A pet.
public struct Pet: Sendable, Equatable, Codable {
    public let petId: String
    public let name: String
    public let species: String
    public let breed: String?
    public let dateOfBirth: Date

    public init(petId: String, name: String, species: String, breed: String?, dateOfBirth: Date) {
        self.petId = petId
        self.name = name
        self.species = species
        self.breed = breed
        self.dateOfBirth = dateOfBirth
    }
}

/// A vaccination record.
public struct Vaccination: Sendable, Equatable, Codable {
    public let petId: String
    public let vaccine: String
    public let administeredUtc: Date
    public let boosterDueUtc: Date?

    public init(petId: String, vaccine: String, administeredUtc: Date, boosterDueUtc: Date?) {
        self.petId = petId
        self.vaccine = vaccine
        self.administeredUtc = administeredUtc
        self.boosterDueUtc = boosterDueUtc
    }
}

/// A weight measurement.
public struct WeightSample: Sendable, Equatable, Codable {
    public let petId: String
    public let weightKg: Double
    public let atUtc: Date

    public init(petId: String, weightKg: Double, atUtc: Date) {
        self.petId = petId
        self.weightKg = weightKg
        self.atUtc = atUtc
    }
}

/// A vet appointment.
public struct VetAppointment: Sendable, Equatable, Codable {
    public let apptId: String
    public let petId: String
    public let reason: String
    public let atUtc: Date
    public let vet: String

    public init(apptId: String, petId: String, reason: String, atUtc: Date, vet: String) {
        self.apptId = apptId
        self.petId = petId
        self.reason = reason
        self.atUtc = atUtc
        self.vet = vet
    }
}

// MARK: - Contract

/// Pets, vaccinations, weight history, and vet appointments for the pets
/// vertical.
public protocol IPetsBoard: AnyObject, Sendable {
    func add(_ p: Pet)
    func getPet(_ id: String) -> Pet?
    var pets: [Pet] { get }
    func recordVaccination(_ v: Vaccination)
    func vaccinationsFor(_ petId: String) -> [Vaccination]
    func recordWeight(_ s: WeightSample)
    func weightHistory(_ petId: String) -> [WeightSample]
    func schedule(_ a: VetAppointment)
    /// Appointments at/after `now`, ordered ascending. `now` is injected so the
    /// behaviour is deterministic (C# uses `DateTimeOffset.UtcNow`).
    func upcomingAppointments(now: Date) -> [VetAppointment]
}

public extension IPetsBoard {
    /// Overload defaulting `now` to the current instant (matches the C# use of
    /// `DateTimeOffset.UtcNow`).
    func upcomingAppointments() -> [VetAppointment] {
        upcomingAppointments(now: Date())
    }
}

// MARK: - InMemoryPetsBoard

/// Deterministic in-memory `IPetsBoard`. All state guarded by a single `NSLock`.
public final class InMemoryPetsBoard: IPetsBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var petsMap: [String: Pet] = [:]
    private var vax: [Vaccination] = []
    private var weights: [WeightSample] = []
    private var appts: [String: VetAppointment] = [:]

    public init() {}

    public func add(_ p: Pet) {
        lock.lock(); defer { lock.unlock() }
        petsMap[p.petId] = p
    }

    public func getPet(_ id: String) -> Pet? {
        lock.lock(); defer { lock.unlock() }
        return petsMap[id]
    }

    public var pets: [Pet] {
        lock.lock(); defer { lock.unlock() }
        return petsMap.values.sorted { $0.name < $1.name }
    }

    public func recordVaccination(_ v: Vaccination) {
        lock.lock(); defer { lock.unlock() }
        vax.append(v)
    }

    public func vaccinationsFor(_ petId: String) -> [Vaccination] {
        lock.lock(); defer { lock.unlock() }
        return vax.filter { $0.petId == petId }.sorted { $0.administeredUtc > $1.administeredUtc }
    }

    public func recordWeight(_ s: WeightSample) {
        lock.lock(); defer { lock.unlock() }
        weights.append(s)
    }

    public func weightHistory(_ petId: String) -> [WeightSample] {
        lock.lock(); defer { lock.unlock() }
        return weights.filter { $0.petId == petId }.sorted { $0.atUtc < $1.atUtc }
    }

    public func schedule(_ a: VetAppointment) {
        lock.lock(); defer { lock.unlock() }
        appts[a.apptId] = a
    }

    public func upcomingAppointments(now: Date) -> [VetAppointment] {
        lock.lock(); defer { lock.unlock() }
        return appts.values.filter { $0.atUtc >= now }.sorted { $0.atUtc < $1.atUtc }
    }
}

// MARK: - PetsDomainContext

/// Static domain-context constants for the pets vertical.
public enum PetsDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Pets] Expert pet care companion. Help with nutrition advice, training techniques (positive reinforcement), health symptom triage (recommend vet for medical decisions), breed-specific care, and emergency first aid basics. Compliance: Animals Protection Act 71/1962, POPIA."
    public static let complianceFlags: [String] = ["Animals_Protection_Act_71_1962", "POPIA", "Vet_Referral_Required"]
    public static let suggestedTools: [String] = ["vet_finder", "pet_health_db", "training_tools", "calendar"]
}
