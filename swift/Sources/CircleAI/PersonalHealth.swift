// PersonalHealth.swift
//
// Port of the Personal.Health vertical from
// src/CircleAI.Personal.Health/PersonalHealthPrimitives.cs and the static
// domain-context constants from PersonalHealthDomainContext.cs:
//   • VitalKind (enum), VitalReading, Allergy, Medication — records
//   • IPersonalHealthBoard        — vitals / allergies / medications
//   • InMemoryPersonalHealthBoard — deterministic in-memory impl
//   • PersonalHealthDomainContext — system-prompt snippet + flags
//
// The Companion-facing wrapper (PersonalHealthCompanionAdapter) is intentionally
// NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `VitalKind` is an `Int`-backed enum so its ordinal (0…7) matches the C#
//     enum's default underlying values; `CaseIterable` mirrors iteration.
//   • `DateTimeOffset` → `Date`; `DateTimeOffset?` (Medication.endedAtUtc) → `Date?`.
//   • `EndMedication` on an unknown med throws → `PersonalHealthError.unknownMedication`.
//   • `ReadSince` orders ascending by time; `Latest` is the newest reading of a
//     kind (or `nil`). `ActiveMedications` = those with no end date, ordered by
//     name.

import Foundation

// MARK: - Enums

/// A kind of vital sign. `Int`-backed so ordinals match the C# enum.
public enum VitalKind: Int, Sendable, Codable, CaseIterable {
    case bloodPressureSystolic = 0
    case bloodPressureDiastolic
    case glucoseMgDl
    case weightKg
    case heartRateBpm
    case temperatureC
    case oxygenPct
    case stepsCount
}

// MARK: - Records

/// A single vital-sign reading.
public struct VitalReading: Sendable, Equatable, Codable {
    /// Which vital was measured.
    public let kind: VitalKind
    /// Measured value.
    public let value: Double
    /// UTC timestamp.
    public let atUtc: Date
    /// Optional note.
    public let note: String?

    public init(kind: VitalKind, value: Double, atUtc: Date, note: String?) {
        self.kind = kind
        self.value = value
        self.atUtc = atUtc
        self.note = note
    }
}

/// A recorded allergy.
public struct Allergy: Sendable, Equatable, Codable {
    /// Stable identifier for the allergy.
    public let allergyId: String
    /// Substance the person is allergic to.
    public let substance: String
    /// Severity (e.g. "mild", "severe").
    public let severity: String

    public init(allergyId: String, substance: String, severity: String) {
        self.allergyId = allergyId
        self.substance = substance
        self.severity = severity
    }
}

/// A medication, optionally still active.
public struct Medication: Sendable, Equatable, Codable {
    /// Stable identifier for the medication.
    public let medId: String
    /// Medication name.
    public let name: String
    /// Dose (e.g. "10mg").
    public let dose: String
    /// Frequency (e.g. "daily").
    public let frequency: String
    /// When the medication was started (UTC).
    public let startedAtUtc: Date
    /// When the medication was stopped (UTC), or `nil` if still active.
    public let endedAtUtc: Date?

    public init(medId: String, name: String, dose: String, frequency: String,
                startedAtUtc: Date, endedAtUtc: Date?) {
        self.medId = medId
        self.name = name
        self.dose = dose
        self.frequency = frequency
        self.startedAtUtc = startedAtUtc
        self.endedAtUtc = endedAtUtc
    }
}

// MARK: - Errors

/// Errors thrown by the personal-health board.
public enum PersonalHealthError: Error, Equatable, CustomStringConvertible {
    /// `endMedication` referenced a med id that is not known.
    case unknownMedication(String)

    public var description: String {
        switch self {
        case .unknownMedication(let id): return "Unknown medication \(id)"
        }
    }
}

// MARK: - IPersonalHealthBoard

/// Vitals, allergies, and medications for the personal-health vertical.
/// User-scoped and never written to a shared store. A synchronous contract —
/// implementations are expected to be thread-safe.
public protocol IPersonalHealthBoard: AnyObject, Sendable {
    /// Records a vital reading.
    func record(_ v: VitalReading)
    /// Readings of `kind` at or after `since`, ascending by time.
    func readSince(kind: VitalKind, since: Date) -> [VitalReading]
    /// Most-recent reading of `kind`, or `nil`.
    func latest(kind: VitalKind) -> VitalReading?
    /// Adds (or replaces, by `allergyId`) an allergy.
    func addAllergy(_ a: Allergy)
    /// All recorded allergies.
    var allergies: [Allergy] { get }
    /// Adds (or replaces, by `medId`) a medication.
    func addMedication(_ m: Medication)
    /// Marks a medication ended. Throws when the med is unknown.
    func endMedication(medId: String, endedAtUtc: Date) throws
    /// Active medications (no end date), ordered by name.
    func activeMedications() -> [Medication]
}

// MARK: - InMemoryPersonalHealthBoard

/// Deterministic in-memory `IPersonalHealthBoard`. All state is guarded by a
/// single `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryPersonalHealthBoard: IPersonalHealthBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var vitals: [VitalReading] = []
    private var allergiesById: [String: Allergy] = [:]
    private var meds: [String: Medication] = [:]

    public init() {}

    public func record(_ v: VitalReading) {
        lock.lock(); defer { lock.unlock() }
        vitals.append(v)
    }

    public func readSince(kind: VitalKind, since: Date) -> [VitalReading] {
        lock.lock(); defer { lock.unlock() }
        return vitals.filter { $0.kind == kind && $0.atUtc >= since }.sorted { $0.atUtc < $1.atUtc }
    }

    public func latest(kind: VitalKind) -> VitalReading? {
        lock.lock(); defer { lock.unlock() }
        return vitals.filter { $0.kind == kind }.max { $0.atUtc < $1.atUtc }
    }

    public func addAllergy(_ a: Allergy) {
        lock.lock(); defer { lock.unlock() }
        allergiesById[a.allergyId] = a
    }

    public var allergies: [Allergy] {
        lock.lock(); defer { lock.unlock() }
        return Array(allergiesById.values)
    }

    public func addMedication(_ m: Medication) {
        lock.lock(); defer { lock.unlock() }
        meds[m.medId] = m
    }

    public func endMedication(medId: String, endedAtUtc: Date) throws {
        lock.lock(); defer { lock.unlock() }
        guard let m = meds[medId] else { throw PersonalHealthError.unknownMedication(medId) }
        meds[medId] = Medication(medId: m.medId, name: m.name, dose: m.dose, frequency: m.frequency,
                                 startedAtUtc: m.startedAtUtc, endedAtUtc: endedAtUtc)
    }

    public func activeMedications() -> [Medication] {
        lock.lock(); defer { lock.unlock() }
        return meds.values.filter { $0.endedAtUtc == nil }.sorted { $0.name < $1.name }
    }
}

// MARK: - PersonalHealthDomainContext

/// Static domain-context constants for the personal-health vertical. Mirrors
/// `PersonalHealthDomainContext` in PersonalHealthDomainContext.cs.
public enum PersonalHealthDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Personal.Health] Personal health and wellness assistant. Help with symptom tracking, appointment preparation, medication reminders, health goal setting, nutrition basics, and health literacy. IMPORTANT: Always recommend consulting a qualified healthcare professional for medical decisions. This is not medical advice. Compliance: POPIA, Health Professions Act."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["POPIA", "Health_Professions_Act", "Not_Medical_Advice"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["health_tracker", "symptom_checker_ref", "calendar", "document_editor"]
}
