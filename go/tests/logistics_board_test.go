// logistics_board_test.go
//
// Verifies the CircleAI.Logistics port (logistics_board.go): shipment/vehicle
// register (blank-id error), GetShipment, Vehicles id-ordering, and PlanRoute
// (distance sum, cost = totalKm*CostPerKm as Decimal, sequential plan ids,
// blank/unknown-vehicle errors).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestLogistics_RegisterAndVehicles(t *testing.T) {
	b := circleai.NewInMemoryLogisticsBoard()
	pickup := time.Date(2026, 7, 1, 6, 0, 0, 0, time.UTC)
	if err := b.RegisterShipment(circleai.Shipment{ShipmentId: "sh1", Origin: "JHB", Destination: "CPT", WeightKg: 500, VolumeM3: 2, Incoterm: "DAP", PickupAtUtc: pickup}); err != nil {
		t.Fatalf("register shipment: %v", err)
	}
	if err := b.RegisterShipment(circleai.Shipment{ShipmentId: " "}); err == nil {
		t.Fatalf("blank ShipmentId must error")
	}
	if err := b.RegisterVehicle(circleai.Vehicle{VehicleId: "v2", CapacityKg: 1000, CapacityM3: 10, CostPerKm: 5}); err != nil {
		t.Fatalf("register vehicle: %v", err)
	}
	if err := b.RegisterVehicle(circleai.Vehicle{VehicleId: "v1", CapacityKg: 2000, CapacityM3: 20, CostPerKm: 8}); err != nil {
		t.Fatalf("register vehicle: %v", err)
	}
	if err := b.RegisterVehicle(circleai.Vehicle{VehicleId: ""}); err == nil {
		t.Fatalf("blank VehicleId must error")
	}

	if s, ok := b.GetShipment("sh1"); !ok || s.Destination != "CPT" {
		t.Fatalf("get shipment = %+v ok=%v", s, ok)
	}
	vs := b.Vehicles()
	if len(vs) != 2 || vs[0].VehicleId != "v1" || vs[1].VehicleId != "v2" {
		t.Fatalf("vehicles id order wrong: %+v", vs)
	}
}

func TestLogistics_PlanRoute(t *testing.T) {
	b := circleai.NewInMemoryLogisticsBoard()
	_ = b.RegisterVehicle(circleai.Vehicle{VehicleId: "v1", CapacityKg: 1000, CapacityM3: 10, CostPerKm: 2.5})

	legs := []circleai.RouteLeg{
		{FromCode: "JHB", ToCode: "BFN", DistanceKm: 400},
		{FromCode: "BFN", ToCode: "CPT", DistanceKm: 600},
	}
	plan, err := b.PlanRoute("v1", legs)
	if err != nil {
		t.Fatalf("plan route: %v", err)
	}
	if plan.TotalDistanceKm != 1000 {
		t.Fatalf("total km = %v, want 1000", plan.TotalDistanceKm)
	}
	// 1000 km * 2.5 = 2500.
	if !plan.EstimatedCost.Equal(circleai.DecimalFromInt(2500)) {
		t.Fatalf("cost = %s, want 2500", plan.EstimatedCost)
	}
	if len(plan.Legs) != 2 {
		t.Fatalf("plan legs = %d, want 2", len(plan.Legs))
	}
	// Second plan gets a distinct sequential id.
	plan2, _ := b.PlanRoute("v1", legs)
	if plan2.PlanId == plan.PlanId {
		t.Fatalf("plan ids should be distinct: %q == %q", plan.PlanId, plan2.PlanId)
	}

	if _, err := b.PlanRoute(" ", legs); err == nil {
		t.Fatalf("blank vehicleId must error")
	}
	if _, err := b.PlanRoute("ghost", legs); err == nil {
		t.Fatalf("unknown vehicle must error")
	}
}
