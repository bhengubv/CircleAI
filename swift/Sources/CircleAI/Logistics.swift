// Logistics.swift
//
// Port of the Logistics vertical from src/CircleAI.Logistics/LogisticsPrimitives.cs
// and the static domain-context constants from LogisticsDomainContext.cs:
//   • Shipment, Vehicle, RouteLeg, RoutePlan — domain records
//   • ILogisticsBoard          — shipments, vehicles, route planning + costing
//   • InMemoryLogisticsBoard   — deterministic in-memory impl
//   • LogisticsDomainContext   — system-prompt snippet + flags
//
// The Companion-facing wrapper (LogisticsCompanionAdapter) is intentionally NOT
// ported.
//
// Porting notes:
//   • `decimal` → `Decimal`; `DateTimeOffset` → `Date`.
//   • Blank ShipmentId / VehicleId / vehicleId throw
//     `LogisticsError.shipmentIdRequired` / `.vehicleIdRequired` /
//     `.vehicleIdArgRequired`. `PlanRoute` on an unknown vehicle throws
//     `.unknownVehicle`.
//   • `Vehicles` is ordered ascending by VehicleId.
//   • `PlanRoute` sums leg distances, computes `cost = totalKm * CostPerKm`
//     (as Decimal), and assigns plan ids `plan-{n}` via an atomic counter.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A shipment to be routed.
public struct Shipment: Sendable, Equatable, Codable {
    public let shipmentId: String
    public let origin: String
    public let destination: String
    public let weightKg: Double
    public let volumeM3: Double
    public let incoterm: String
    public let pickupAtUtc: Date

    public init(shipmentId: String, origin: String, destination: String, weightKg: Double,
                volumeM3: Double, incoterm: String, pickupAtUtc: Date) {
        self.shipmentId = shipmentId
        self.origin = origin
        self.destination = destination
        self.weightKg = weightKg
        self.volumeM3 = volumeM3
        self.incoterm = incoterm
        self.pickupAtUtc = pickupAtUtc
    }
}

/// A vehicle available for routing.
public struct Vehicle: Sendable, Equatable, Codable {
    public let vehicleId: String
    public let capacityKg: Double
    public let capacityM3: Double
    public let costPerKm: Double

    public init(vehicleId: String, capacityKg: Double, capacityM3: Double, costPerKm: Double) {
        self.vehicleId = vehicleId
        self.capacityKg = capacityKg
        self.capacityM3 = capacityM3
        self.costPerKm = costPerKm
    }
}

/// A single leg of a route.
public struct RouteLeg: Sendable, Equatable, Codable {
    public let fromCode: String
    public let toCode: String
    public let distanceKm: Double

    public init(fromCode: String, toCode: String, distanceKm: Double) {
        self.fromCode = fromCode
        self.toCode = toCode
        self.distanceKm = distanceKm
    }
}

/// A planned route with its total distance and estimated cost.
public struct RoutePlan: Sendable, Equatable, Codable {
    public let planId: String
    public let vehicleId: String
    public let legs: [RouteLeg]
    public let totalDistanceKm: Double
    public let estimatedCost: Decimal

    public init(planId: String, vehicleId: String, legs: [RouteLeg], totalDistanceKm: Double, estimatedCost: Decimal) {
        self.planId = planId
        self.vehicleId = vehicleId
        self.legs = legs
        self.totalDistanceKm = totalDistanceKm
        self.estimatedCost = estimatedCost
    }
}

// MARK: - Errors

public enum LogisticsError: Error, Equatable, CustomStringConvertible {
    case shipmentIdRequired
    case vehicleIdRequired
    case vehicleIdArgRequired
    case unknownVehicle(String)

    public var description: String {
        switch self {
        case .shipmentIdRequired: return "ShipmentId required"
        case .vehicleIdRequired: return "VehicleId required"
        case .vehicleIdArgRequired: return "vehicleId required"
        case .unknownVehicle(let id): return "Unknown vehicle '\(id)'."
        }
    }
}

// MARK: - Contract

/// Shipments, vehicles, and route planning for the logistics vertical.
public protocol ILogisticsBoard: AnyObject, Sendable {
    func registerShipment(_ s: Shipment) throws
    func registerVehicle(_ v: Vehicle) throws
    func getShipment(_ id: String) -> Shipment?
    var vehicles: [Vehicle] { get }
    func planRoute(vehicleId: String, legs: [RouteLeg]) throws -> RoutePlan
}

// MARK: - InMemoryLogisticsBoard

/// Deterministic in-memory `ILogisticsBoard`. All state guarded by a single
/// `NSLock`.
public final class InMemoryLogisticsBoard: ILogisticsBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var shipments: [String: Shipment] = [:]
    private var vehiclesMap: [String: Vehicle] = [:]
    private var seq: Int64 = 0

    public init() {}

    public func registerShipment(_ s: Shipment) throws {
        if s.shipmentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw LogisticsError.shipmentIdRequired }
        lock.lock(); defer { lock.unlock() }
        shipments[s.shipmentId] = s
    }

    public func registerVehicle(_ v: Vehicle) throws {
        if v.vehicleId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw LogisticsError.vehicleIdRequired }
        lock.lock(); defer { lock.unlock() }
        vehiclesMap[v.vehicleId] = v
    }

    public func getShipment(_ id: String) -> Shipment? {
        lock.lock(); defer { lock.unlock() }
        return shipments[id]
    }

    public var vehicles: [Vehicle] {
        lock.lock(); defer { lock.unlock() }
        return vehiclesMap.values.sorted { $0.vehicleId < $1.vehicleId }
    }

    public func planRoute(vehicleId: String, legs: [RouteLeg]) throws -> RoutePlan {
        if vehicleId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw LogisticsError.vehicleIdArgRequired }
        lock.lock(); defer { lock.unlock() }
        guard let vehicle = vehiclesMap[vehicleId] else { throw LogisticsError.unknownVehicle(vehicleId) }
        let totalKm = legs.reduce(0.0) { $0 + $1.distanceKm }
        let cost = Decimal(totalKm * vehicle.costPerKm)
        seq += 1
        return RoutePlan(planId: "plan-\(seq)", vehicleId: vehicleId, legs: legs,
                         totalDistanceKm: totalKm, estimatedCost: cost)
    }
}

// MARK: - LogisticsDomainContext

/// Static domain-context constants for the logistics vertical.
public enum LogisticsDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Logistics] Expert logistics and supply chain assistant. Help with route optimisation, fleet maintenance scheduling, customs documentation, incoterms, 3PL management, warehouse layout, and last-mile delivery strategy. Apply cost-per-km and load efficiency metrics. Compliance: RTMS, SARS customs regulations, AARTO, POPIA."
    public static let complianceFlags: [String] = ["RTMS", "SARS_Customs", "AARTO", "POPIA", "Incoterms_2020"]
    public static let suggestedTools: [String] = ["route_planner", "fleet_tracker", "customs_portal", "analytics"]
}
