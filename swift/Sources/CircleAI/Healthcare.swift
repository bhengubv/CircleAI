// Healthcare.swift
//
// Port of the Healthcare vertical's domain primitives from
// src/CircleAI.Healthcare/HealthcarePrimitives.cs and the static
// domain-context constants from HealthcareDomainContext.cs:
//   • Patient, HealthAppointment, Prescription — domain records
//   • IHealthcareBoard                         — patient / appointment /
//                                                 prescription contract
//   • InMemoryHealthcareBoard                  — deterministic in-memory impl
//   • HealthcareDomainContext                  — system-prompt snippet + flags
//
// The Companion-facing wrapper (HealthcareCompanionAdapter) is intentionally
// NOT ported in this wave — following the SafetyChild precedent, it is a thin
// string-prefixing shim over ICompanionSession.agent(...) LLM calls, carries no
// deterministic behaviour to test, and lies outside this work unit's enumerated
// board + record types.
//
// Porting notes:
//   • `DateTime` / `DateTimeOffset` → `Date`.
//   • `ConcurrentDictionary` collapses to plain dictionaries guarded by a single
//     `NSLock`; every accessor returns an immutable snapshot.
//   • `UpdateStatus` on an unknown appointment throws in C#
//     (`InvalidOperationException`); that maps onto `HealthcareError.unknownAppointment`.
//   • `AppointmentsFor` orders ascending by `atUtc`; `PrescriptionsFor` orders
//     descending by `prescribedUtc`. Swift's `sorted` is stable, matching LINQ's
//     stable `OrderBy` / `OrderByDescending`.

import Foundation

// MARK: - Records

/// A patient in the healthcare board.
public struct Patient: Sendable, Equatable, Codable {
    /// Stable identifier for the patient.
    public let patientId: String
    /// Patient's name.
    public let name: String
    /// Date of birth.
    public let dateOfBirth: Date

    public init(patientId: String, name: String, dateOfBirth: Date) {
        self.patientId = patientId
        self.name = name
        self.dateOfBirth = dateOfBirth
    }
}

/// A scheduled clinical appointment.
public struct HealthAppointment: Sendable, Equatable, Codable {
    /// Stable identifier for the appointment.
    public let apptId: String
    /// Identifier of the patient the appointment is for.
    public let patientId: String
    /// Provider name.
    public let provider: String
    /// Appointment time (UTC).
    public let atUtc: Date
    /// Free-form status (e.g. "booked", "seen", "cancelled").
    public let status: String

    public init(apptId: String, patientId: String, provider: String, atUtc: Date, status: String) {
        self.apptId = apptId
        self.patientId = patientId
        self.provider = provider
        self.atUtc = atUtc
        self.status = status
    }
}

/// A prescription written for a patient.
public struct Prescription: Sendable, Equatable, Codable {
    /// Stable identifier for the prescription.
    public let rxId: String
    /// Identifier of the patient the prescription is for.
    public let patientId: String
    /// Medication name.
    public let medicationName: String
    /// Dose (e.g. "500mg").
    public let dose: String
    /// Frequency (e.g. "twice daily").
    public let frequency: String
    /// When the prescription was written (UTC).
    public let prescribedUtc: Date

    public init(rxId: String, patientId: String, medicationName: String, dose: String,
                frequency: String, prescribedUtc: Date) {
        self.rxId = rxId
        self.patientId = patientId
        self.medicationName = medicationName
        self.dose = dose
        self.frequency = frequency
        self.prescribedUtc = prescribedUtc
    }
}

// MARK: - Errors

/// Errors thrown by the healthcare board.
public enum HealthcareError: Error, Equatable, CustomStringConvertible {
    /// `updateStatus` was called for an appointment id that is not known.
    case unknownAppointment(String)

    public var description: String {
        switch self {
        case .unknownAppointment(let id): return "Unknown appointment \(id)"
        }
    }
}

// MARK: - IHealthcareBoard

/// Patient registration, appointment scheduling, and prescriptions for the
/// healthcare vertical. A synchronous contract — implementations are expected to
/// be thread-safe.
public protocol IHealthcareBoard: AnyObject, Sendable {
    /// Registers (or replaces, by `patientId`) a patient.
    func register(_ p: Patient)
    /// Returns the patient with `id`, or `nil` if none is registered.
    func getPatient(_ id: String) -> Patient?
    /// Schedules (or replaces, by `apptId`) an appointment.
    func schedule(_ a: HealthAppointment)
    /// Updates the status of an existing appointment.
    /// Throws `HealthcareError.unknownAppointment` when `apptId` is unknown.
    func updateStatus(apptId: String, status: String) throws
    /// Appointments for `patientId`, ordered ascending by time.
    func appointmentsFor(patientId: String) -> [HealthAppointment]
    /// Records (or replaces, by `rxId`) a prescription.
    func prescribe(_ r: Prescription)
    /// Prescriptions for `patientId`, most-recent first.
    func prescriptionsFor(patientId: String) -> [Prescription]
}

// MARK: - InMemoryHealthcareBoard

/// Deterministic in-memory `IHealthcareBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryHealthcareBoard: IHealthcareBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var patients: [String: Patient] = [:]
    private var appts: [String: HealthAppointment] = [:]
    private var rx: [String: Prescription] = [:]

    public init() {}

    public func register(_ p: Patient) {
        lock.lock(); defer { lock.unlock() }
        patients[p.patientId] = p
    }

    public func getPatient(_ id: String) -> Patient? {
        lock.lock(); defer { lock.unlock() }
        return patients[id]
    }

    public func schedule(_ a: HealthAppointment) {
        lock.lock(); defer { lock.unlock() }
        appts[a.apptId] = a
    }

    public func updateStatus(apptId: String, status: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let a = appts[apptId] else { throw HealthcareError.unknownAppointment(apptId) }
        appts[apptId] = HealthAppointment(apptId: a.apptId, patientId: a.patientId,
                                          provider: a.provider, atUtc: a.atUtc, status: status)
    }

    public func appointmentsFor(patientId: String) -> [HealthAppointment] {
        lock.lock(); defer { lock.unlock() }
        return appts.values
            .filter { $0.patientId == patientId }
            .sorted { $0.atUtc < $1.atUtc }
    }

    public func prescribe(_ r: Prescription) {
        lock.lock(); defer { lock.unlock() }
        rx[r.rxId] = r
    }

    public func prescriptionsFor(patientId: String) -> [Prescription] {
        lock.lock(); defer { lock.unlock() }
        return rx.values
            .filter { $0.patientId == patientId }
            .sorted { $0.prescribedUtc > $1.prescribedUtc }
    }
}

// MARK: - HealthcareDomainContext

/// Static domain-context constants for the healthcare vertical. Mirrors
/// `HealthcareDomainContext` in HealthcareDomainContext.cs.
public enum HealthcareDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Healthcare] You are a healthcare operations and clinical knowledge assistant. Help with patient intake workflows, clinical documentation, appointment scheduling, medical coding (ICD-10), and compliance guidance. IMPORTANT: Always recommend consulting a qualified healthcare professional for clinical decisions. This is a support tool, not a diagnostic system. Compliance: HIPAA, POPIA, Health Professions Act, NHA."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["HIPAA", "POPIA", "Health_Professions_Act_56_1974", "NHA_61_2003", "ICD10"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["ehr_system", "appointment_scheduler", "document_editor", "icd10_lookup"]
}
