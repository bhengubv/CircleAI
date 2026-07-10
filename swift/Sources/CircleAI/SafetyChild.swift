// SafetyChild.swift
//
// Port of the child-safety vertical's domain primitives from
// src/CircleAI.Safety.Child/ChildSafetyPrimitives.cs:
//   • TrustedAdult, Geofence, CheckIn — domain records
//   • IChildSafetyBoard               — trusted-adult ring / geofence / check-in
//                                        contract
//   • InMemoryChildSafetyBoard        — deterministic in-memory implementation,
//                                        including the Haversine fence test
//
// The Companion-facing pieces of this C# project (SafetyChildCompanionAdapter,
// SafetyChildDomainContext) are intentionally NOT ported in this wave — they are
// outside this work unit's enumerated types and depend on Companion agent
// methods. Only the deterministic in-memory board + its records are in scope.
//
// The C# namespace is `CircleAI.Safety.Child`; the flat Swift module keeps the
// distinct type names (`TrustedAdult`, `Geofence`, `CheckIn`) so there is no
// collision with the `CircleAI.Safety` types in Safety.swift.
//
// Porting notes:
//   • `RecentCheckIns` throws `ArgumentOutOfRangeException` when `limit <= 0`;
//     that maps onto `ChildSafetyError.limitOutOfRange` on the one `throws`
//     board method. Every other board member is non-throwing.
//   • The Haversine helper reproduces the C# constants exactly:
//     R = 6_371_000 m, degrees→radians via `d * .pi / 180`.
//   • `ConcurrentDictionary` (adults, fences) + `List` + `lock` (check-ins)
//     collapse to a single NSLock guarding all state.

import Foundation

// MARK: - Records

/// A trusted adult in a child's safety ring.
public struct TrustedAdult: Sendable, Equatable, Codable {
    /// Stable identifier for the adult.
    public let adultId: String
    /// Adult's name.
    public let name: String
    /// Adult's phone number.
    public let phone: String
    /// Relationship to the child (e.g. "parent", "aunt", "coach").
    public let relationship: String
    /// Position in the escalation ring; lower is contacted first.
    public let ringPriority: Int

    public init(adultId: String, name: String, phone: String, relationship: String, ringPriority: Int) {
        self.adultId = adultId
        self.name = name
        self.phone = phone
        self.relationship = relationship
        self.ringPriority = ringPriority
    }
}

/// A circular geofence.
public struct Geofence: Sendable, Equatable, Codable {
    /// Stable identifier for the fence.
    public let fenceId: String
    /// Human-readable name (e.g. "home", "school").
    public let name: String
    /// Centre latitude in degrees.
    public let centreLat: Double
    /// Centre longitude in degrees.
    public let centreLon: Double
    /// Fence radius in metres.
    public let radiusMeters: Double

    public init(fenceId: String, name: String, centreLat: Double, centreLon: Double, radiusMeters: Double) {
        self.fenceId = fenceId
        self.name = name
        self.centreLat = centreLat
        self.centreLon = centreLon
        self.radiusMeters = radiusMeters
    }
}

/// A child check-in event, optionally geotagged.
public struct CheckIn: Sendable, Equatable, Codable {
    /// Identifier of the child checking in.
    public let childId: String
    /// Free-form status (e.g. "arrived", "safe", "help").
    public let status: String
    /// Latitude in degrees, or `nil` if not geotagged.
    public let lat: Double?
    /// Longitude in degrees, or `nil` if not geotagged.
    public let lon: Double?
    /// UTC timestamp of the check-in.
    public let atUtc: Date

    public init(childId: String, status: String, lat: Double?, lon: Double?, atUtc: Date) {
        self.childId = childId
        self.status = status
        self.lat = lat
        self.lon = lon
        self.atUtc = atUtc
    }
}

// MARK: - Errors

/// Errors thrown by the child-safety board. Mirrors the C#
/// `ArgumentOutOfRangeException` thrown by `RecentCheckIns`.
public enum ChildSafetyError: Error, Equatable, CustomStringConvertible {
    /// `limit` was less than or equal to zero.
    case limitOutOfRange

    public var description: String {
        switch self {
        case .limitOutOfRange: return "limit must be greater than zero"
        }
    }
}

// MARK: - IChildSafetyBoard

/// Trusted-adult ring, geofences, and check-in history for the child-safety
/// vertical. A synchronous contract — implementations are expected to be
/// thread-safe.
public protocol IChildSafetyBoard: AnyObject, Sendable {
    /// Adds (or replaces, by `adultId`) a trusted adult.
    func addAdult(_ a: TrustedAdult)

    /// Trusted adults ordered by ascending `ringPriority`.
    var ringOrdered: [TrustedAdult] { get }

    /// Defines (or replaces, by `fenceId`) a geofence.
    func defineGeofence(_ g: Geofence)

    /// Returns the geofence with `id`, or `nil` if none is defined.
    func getGeofence(_ id: String) -> Geofence?

    /// Returns `true` if `(lat, lon)` lies within any defined geofence.
    func isInsideAnyFence(lat: Double, lon: Double) -> Bool

    /// Records a check-in event.
    func recordCheckIn(_ c: CheckIn)

    /// Returns up to `limit` most-recent check-ins for `childId`, newest first.
    /// Throws `ChildSafetyError.limitOutOfRange` when `limit <= 0`.
    func recentCheckIns(childId: String, limit: Int) throws -> [CheckIn]
}

public extension IChildSafetyBoard {
    /// Overload matching the C# default `limit = 20`.
    func recentCheckIns(childId: String) throws -> [CheckIn] {
        try recentCheckIns(childId: childId, limit: 20)
    }
}

// MARK: - InMemoryChildSafetyBoard

/// Deterministic in-memory `IChildSafetyBoard`. All state is guarded by a single
/// `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryChildSafetyBoard: IChildSafetyBoard, @unchecked Sendable {
    private let lock = NSLock()
    // Adults / fences keyed by id, last-write-wins (matches the C#
    // ConcurrentDictionary semantics).
    private var adultsById: [String: TrustedAdult] = [:]
    private var fencesById: [String: Geofence] = [:]
    private var checkIns: [CheckIn] = []

    public init() {}

    public func addAdult(_ a: TrustedAdult) {
        lock.lock(); defer { lock.unlock() }
        adultsById[a.adultId] = a
    }

    public var ringOrdered: [TrustedAdult] {
        lock.lock(); defer { lock.unlock() }
        // Stable ascending sort by ring priority (Swift `sorted` is stable).
        return Array(adultsById.values).sorted { $0.ringPriority < $1.ringPriority }
    }

    public func defineGeofence(_ g: Geofence) {
        lock.lock(); defer { lock.unlock() }
        fencesById[g.fenceId] = g
    }

    public func getGeofence(_ id: String) -> Geofence? {
        lock.lock(); defer { lock.unlock() }
        return fencesById[id]
    }

    public func isInsideAnyFence(lat: Double, lon: Double) -> Bool {
        lock.lock()
        let fences = Array(fencesById.values)
        lock.unlock()
        for g in fences {
            if Self.haversineMeters(g.centreLat, g.centreLon, lat, lon) <= g.radiusMeters {
                return true
            }
        }
        return false
    }

    public func recordCheckIn(_ c: CheckIn) {
        lock.lock(); defer { lock.unlock() }
        checkIns.append(c)
    }

    public func recentCheckIns(childId: String, limit: Int) throws -> [CheckIn] {
        if limit <= 0 { throw ChildSafetyError.limitOutOfRange }
        lock.lock(); defer { lock.unlock() }
        return checkIns
            .filter { $0.childId == childId }
            .sorted { $0.atUtc > $1.atUtc }
            .prefix(limit)
            .map { $0 }
    }

    // ── Private ─────────────────────────────────────────────────────────────

    /// Great-circle distance in metres between two lat/lon points. Reproduces the
    /// C# `HaversineMeters` exactly (Earth radius 6 371 000 m).
    private static func haversineMeters(_ aLat: Double, _ aLon: Double, _ bLat: Double, _ bLon: Double) -> Double {
        let R = 6_371_000.0
        func degToRad(_ d: Double) -> Double { d * Double.pi / 180.0 }
        let dLat = degToRad(bLat - aLat)
        let dLon = degToRad(bLon - aLon)
        let s1 = sin(dLat / 2)
        let s2 = sin(dLon / 2)
        let a = s1 * s1 + cos(degToRad(aLat)) * cos(degToRad(bLat)) * s2 * s2
        let c = 2 * atan2(sqrt(a), sqrt(1 - a))
        return R * c
    }
}
