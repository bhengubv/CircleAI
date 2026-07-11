// logistics_board.test.ts
// Verifies the CircleAI.Logistics port: shipment + vehicle registration
// (id-ordered vehicles) and the distance × cost-per-km route planner.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryLogisticsBoard,
  LogisticsDomainContext,
  shipment,
  vehicle,
  routeLeg,
} from "../src/logistics/index";

describe("InMemoryLogisticsBoard", () => {
  it("registers shipments and vehicles (vehicles ordered by id)", () => {
    const b = new InMemoryLogisticsBoard();
    b.registerShipment(shipment("s1", "JNB", "CPT", 1000, 5, "EXW", new Date("2026-05-01T00:00:00Z")));
    b.registerVehicle(vehicle("vB", 5000, 30, 12));
    b.registerVehicle(vehicle("vA", 3000, 20, 9));
    assert.equal(b.getShipment("s1")?.destination, "CPT");
    assert.deepEqual(
      b.vehicles.map((v) => v.vehicleId),
      ["vA", "vB"],
    );
    assert.throws(() => b.registerShipment(shipment("", "a", "b", 1, 1, "x", new Date())), /ShipmentId required/);
    assert.throws(() => b.registerVehicle(vehicle("", 1, 1, 1)), /VehicleId required/);
  });

  it("plans routes: total distance and cost, unknown vehicle throws", () => {
    const b = new InMemoryLogisticsBoard();
    b.registerVehicle(vehicle("v1", 3000, 20, 10));
    const plan = b.planRoute("v1", [routeLeg("JNB", "BFN", 400), routeLeg("BFN", "CPT", 1000)]);
    assert.equal(plan.totalDistanceKm, 1400);
    assert.equal(plan.estimatedCost, 14000); // 1400 * 10
    assert.equal(plan.planId, "plan-1");
    assert.equal(plan.legs.length, 2);
    const plan2 = b.planRoute("v1", [routeLeg("A", "B", 100)]);
    assert.equal(plan2.planId, "plan-2");
    assert.throws(() => b.planRoute("ghost", []), /Unknown vehicle 'ghost'/);
    assert.throws(() => b.planRoute(" ", []), /vehicleId required/);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(LogisticsDomainContext.systemPromptSnippet.includes("[DOMAIN: Logistics]"));
    assert.deepEqual(LogisticsDomainContext.complianceFlags, ["RTMS", "SARS_Customs", "AARTO", "POPIA", "Incoterms_2020"]);
    assert.deepEqual(LogisticsDomainContext.suggestedTools, ["route_planner", "fleet_tracker", "customs_portal", "analytics"]);
  });
});
