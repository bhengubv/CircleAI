// Safety.swift
//
// Port of the personal-safety vertical's domain primitives from
// src/CircleAI.Safety/SafetyPrimitives.cs:
//   • IncidentSeverity        — Info / Warning / Critical / Emergency
//   • Incident, Hazard, EmergencyContact  — domain records
//   • ISafetyBoard            — incident/hazard/contact board contract
//   • InMemorySafetyBoard     — deterministic in-memory implementation
//
// The Companion-facing pieces of this C# project (SafetyCompanionAdapter,
// SafetyDomainContext) are intentionally NOT ported in this wave: they depend on
// Companion agent methods and a domain-context surface outside this work unit's
// enumerated types. Only the deterministic in-memory board + its records are in
// scope here.
//
// Porting notes:
//   • `IncidentSeverity` is Int-backed AND Comparable so the C# comparison
//     `(int)i.Severity >= (int)minimum` in `AtOrAboveSeverity` translates
//     directly. Ordinals follow the C# declaration order.
//   • `double?` latitude/longitude → `Double?`.
//   • `ISafetyBoard` is a SYNCHRONOUS contract (the C# members are non-async
//     `void`/property members). `InMemorySafetyBoard` is `final class ...
//     @unchecked Sendable` with a single NSLock guarding all state — equivalent
//     to the C# mix of `lock (_lock)` + `ConcurrentDictionary` (a single lock
//     subsumes the concurrent dictionary and avoids lock-ordering hazards).
//   • Ordering uses Swift's stable `sorted(by:)` (stable since Swift 5) so that,
//     like .NET's stable `OrderByDescending`, ties preserve insertion order.

import Foundation

// MARK: - IncidentSeverity

/// Severity of a logged safety incident.
///
/// Ordinals follow the C# `enum IncidentSeverity { Info, Warning, Critical,
/// Emergency }` order and drive `AtOrAboveSeverity` comparisons — append, never
/// reorder.
public enum IncidentSeverity: Int, Codable, Sendable, Comparable, CaseIterable {
    /// Informational — no action required.
    case info = 0
    /// Warning — worth noting, not urgent.
    case warning = 1
    /// Critical — needs prompt attention.
    case critical = 2
    /// Emergency — life-safety; escalate immediately.
    case emergency = 3

    public static func < (lhs: IncidentSeverity, rhs: IncidentSeverity) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

// MARK: - Records

/// A logged safety incident, optionally geotagged.
public struct Incident: Sendable, Equatable, Codable {
    /// Stable identifier for the incident.
    public let incidentId: String
    /// Assessed severity.
    public let severity: IncidentSeverity
    /// Human-readable description.
    public let description: String
    /// Latitude in degrees, or `nil` if not geotagged.
    public let latitude: Double?
    /// Longitude in degrees, or `nil` if not geotagged.
    public let longitude: Double?
    /// UTC timestamp of the incident.
    public let atUtc: Date

    public init(
        incidentId: String,
        severity: IncidentSeverity,
        description: String,
        latitude: Double?,
        longitude: Double?,
        atUtc: Date
    ) {
        self.incidentId = incidentId
        self.severity = severity
        self.description = description
        self.latitude = latitude
        self.longitude = longitude
        self.atUtc = atUtc
    }
}

/// A noted environmental hazard.
public struct Hazard: Sendable, Equatable, Codable {
    /// Stable identifier for the hazard.
    public let hazardId: String
    /// Human-readable description.
    public let description: String
    /// Category label (e.g. "fire", "flood", "structural").
    public let category: String
    /// UTC timestamp when the hazard was noted.
    public let notedUtc: Date

    public init(hazardId: String, description: String, category: String, notedUtc: Date) {
        self.hazardId = hazardId
        self.description = description
        self.category = category
        self.notedUtc = notedUtc
    }
}

/// An emergency contact in the safety ring.
public struct EmergencyContact: Sendable, Equatable, Codable {
    /// Stable identifier for the contact.
    public let contactId: String
    /// Contact's name.
    public let name: String
    /// Contact's phone number.
    public let phone: String
    /// Relationship to the user (e.g. "spouse", "neighbour").
    public let relationship: String

    public init(contactId: String, name: String, phone: String, relationship: String) {
        self.contactId = contactId
        self.name = name
        self.phone = phone
        self.relationship = relationship
    }
}

// MARK: - ISafetyBoard

/// Incident, hazard, and emergency-contact board for the personal-safety
/// vertical. A synchronous contract — implementations are expected to be
/// thread-safe.
public protocol ISafetyBoard: AnyObject, Sendable {
    /// Logs an incident.
    func log(_ i: Incident)

    /// All logged incidents, newest first.
    var active: [Incident] { get }

    /// Incidents at or above `minimum` severity, newest first.
    func atOrAboveSeverity(_ minimum: IncidentSeverity) -> [Incident]

    /// Notes a hazard. Re-noting the same `hazardId` replaces the prior entry.
    func noteHazard(_ h: Hazard)

    /// All noted hazards, newest first.
    var hazards: [Hazard] { get }

    /// Adds an emergency contact.
    func addContact(_ c: EmergencyContact)

    /// The first-added emergency contact, or `nil` if none.
    var firstContact: EmergencyContact? { get }

    /// All emergency contacts in insertion order.
    var contacts: [EmergencyContact] { get }
}

// MARK: - InMemorySafetyBoard

/// Deterministic in-memory `ISafetyBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemorySafetyBoard: ISafetyBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var incidents: [Incident] = []
    // Hazards keyed by id, last-write-wins (matches the C# ConcurrentDictionary).
    private var hazardsById: [String: Hazard] = [:]
    private var contactsList: [EmergencyContact] = []

    public init() {}

    public func log(_ i: Incident) {
        lock.lock(); defer { lock.unlock() }
        incidents.append(i)
    }

    public var active: [Incident] {
        lock.lock(); defer { lock.unlock() }
        return incidents.sorted { $0.atUtc > $1.atUtc }
    }

    public func atOrAboveSeverity(_ minimum: IncidentSeverity) -> [Incident] {
        lock.lock(); defer { lock.unlock() }
        return incidents
            .filter { $0.severity.rawValue >= minimum.rawValue }
            .sorted { $0.atUtc > $1.atUtc }
    }

    public func noteHazard(_ h: Hazard) {
        lock.lock(); defer { lock.unlock() }
        hazardsById[h.hazardId] = h
    }

    public var hazards: [Hazard] {
        lock.lock(); defer { lock.unlock() }
        return Array(hazardsById.values).sorted { $0.notedUtc > $1.notedUtc }
    }

    public func addContact(_ c: EmergencyContact) {
        lock.lock(); defer { lock.unlock() }
        contactsList.append(c)
    }

    public var firstContact: EmergencyContact? {
        lock.lock(); defer { lock.unlock() }
        return contactsList.first
    }

    public var contacts: [EmergencyContact] {
        lock.lock(); defer { lock.unlock() }
        return contactsList
    }
}
