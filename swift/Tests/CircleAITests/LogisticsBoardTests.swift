// LogisticsBoardTests.swift
//
// Exercises the Logistics records' Codable round-trips and the deterministic
// behaviour of InMemoryLogisticsBoard — shipment/vehicle registration (incl.
// blank-id throws), vehicle listing (id-ordered), and route planning (distance
// sum, cost = km * costPerKm, monotonic plan ids, unknown-vehicle throw). Also
// checks the LogisticsDomainContext constants. Mirrors CircleAI.Logistics/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class LogisticsBoardTests: XCTestCase {

    private func vehicle(_ id: String, costPerKm: Double = 2.0) -> Vehicle {
        Vehicle(vehicleId: id, capacityKg: 1000, capacityM3: 10, costPerKm: costPerKm)
    }
    private func shipment(_ id: String) -> Shipment {
        Shipment(shipmentId: id, origin: "JHB", destination: "CPT", weightKg: 5, volumeM3: 1,
                 incoterm: "DAP", pickupAtUtc: Date(timeIntervalSince1970: 0))
    }

    func testShipmentAndRoutePlanCodableRoundTrip() throws {
        let s = shipment("s1")
        XCTAssertEqual(try JSONDecoder().decode(Shipment.self, from: try JSONEncoder().encode(s)), s)
        let plan = RoutePlan(planId: "plan-1", vehicleId: "v1",
                             legs: [RouteLeg(fromCode: "A", toCode: "B", distanceKm: 5)],
                             totalDistanceKm: 5, estimatedCost: 10)
        XCTAssertEqual(try JSONDecoder().decode(RoutePlan.self, from: try JSONEncoder().encode(plan)), plan)
    }

    func testRegisterGetShipmentAndVehiclesOrdered() throws {
        let b = InMemoryLogisticsBoard()
        try b.registerShipment(shipment("s1"))
        XCTAssertEqual(b.getShipment("s1")?.destination, "CPT")
        XCTAssertNil(b.getShipment("nope"))
        try b.registerVehicle(vehicle("v2"))
        try b.registerVehicle(vehicle("v1"))
        XCTAssertEqual(b.vehicles.map { $0.vehicleId }, ["v1", "v2"])
    }

    func testBlankIdsThrow() {
        let b = InMemoryLogisticsBoard()
        XCTAssertThrowsError(try b.registerShipment(shipment(" "))) { XCTAssertEqual($0 as? LogisticsError, .shipmentIdRequired) }
        XCTAssertThrowsError(try b.registerVehicle(vehicle("  "))) { XCTAssertEqual($0 as? LogisticsError, .vehicleIdRequired) }
    }

    func testPlanRouteComputesDistanceCostAndMonotonicIds() throws {
        let b = InMemoryLogisticsBoard()
        try b.registerVehicle(vehicle("v1", costPerKm: 2.5))
        let legs = [
            RouteLeg(fromCode: "A", toCode: "B", distanceKm: 100),
            RouteLeg(fromCode: "B", toCode: "C", distanceKm: 50)
        ]
        let plan1 = try b.planRoute(vehicleId: "v1", legs: legs)
        XCTAssertEqual(plan1.planId, "plan-1")
        XCTAssertEqual(plan1.totalDistanceKm, 150)
        XCTAssertEqual(plan1.estimatedCost, Decimal(375))   // 150 * 2.5
        XCTAssertEqual(plan1.legs.count, 2)
        let plan2 = try b.planRoute(vehicleId: "v1", legs: [])
        XCTAssertEqual(plan2.planId, "plan-2")
        XCTAssertEqual(plan2.totalDistanceKm, 0)
        XCTAssertEqual(plan2.estimatedCost, Decimal(0))
    }

    func testPlanRouteBlankVehicleAndUnknownVehicleThrow() throws {
        let b = InMemoryLogisticsBoard()
        XCTAssertThrowsError(try b.planRoute(vehicleId: " ", legs: [])) { XCTAssertEqual($0 as? LogisticsError, .vehicleIdArgRequired) }
        XCTAssertThrowsError(try b.planRoute(vehicleId: "ghost", legs: [])) { XCTAssertEqual($0 as? LogisticsError, .unknownVehicle("ghost")) }
    }

    func testDomainContext() {
        XCTAssertTrue(LogisticsDomainContext.systemPromptSnippet.contains("[DOMAIN: Logistics]"))
        XCTAssertEqual(LogisticsDomainContext.complianceFlags, ["RTMS", "SARS_Customs", "AARTO", "POPIA", "Incoterms_2020"])
        XCTAssertEqual(LogisticsDomainContext.suggestedTools, ["route_planner", "fleet_tracker", "customs_portal", "analytics"])
    }
}
