// logistics_board.go
//
// Ports the CircleAI.Logistics primitive vertical (LogisticsPrimitives.cs):
//   Shipment / Vehicle / RouteLeg / RoutePlan (records) -> value structs
//   ILogisticsBoard        -> LogisticsBoard interface (I-prefix dropped)
//   InMemoryLogisticsBoard -> InMemoryLogisticsBoard
//
// The LogisticsDomainContext (static prompt strings) and LogisticsCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: Vehicles orders by VehicleId (C# OrderBy(VehicleId),
// culture-sensitive default comparer -> cultureLess over the ASCII ids).
// PlanRoute sums leg distances, computes cost = totalKm * CostPerKm as a double
// then converts to Decimal (the C# `(decimal)(double)` cast -> DecimalFromFloat),
// mints sequential "plan-N" ids via an atomic counter, and defensively copies the
// leg list into the returned plan.

package circleai

import (
	"errors"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Shipment is a shipment. Ports the Shipment record.
type Shipment struct {
	ShipmentId  string
	Origin      string
	Destination string
	WeightKg    float64
	VolumeM3    float64
	Incoterm    string
	PickupAtUtc time.Time
}

// Vehicle is a transport vehicle. Ports the Vehicle record.
type Vehicle struct {
	VehicleId  string
	CapacityKg float64
	CapacityM3 float64
	CostPerKm  float64
}

// RouteLeg is one hop of a route. Ports the RouteLeg record.
type RouteLeg struct {
	FromCode   string
	ToCode     string
	DistanceKm float64
}

// RoutePlan is a costed multi-leg route. Ports the RoutePlan record. Legs mirrors
// the C# IReadOnlyList<RouteLeg>; EstimatedCost uses the shared exact Decimal.
type RoutePlan struct {
	PlanId          string
	VehicleId       string
	Legs            []RouteLeg
	TotalDistanceKm float64
	EstimatedCost   Decimal
}

// LogisticsBoard is the shipments/vehicles/routing board. Ports ILogisticsBoard.
// Vehicles is exposed as a method.
type LogisticsBoard interface {
	// RegisterShipment stores a shipment; errors on a blank ShipmentId.
	RegisterShipment(s Shipment) error
	// RegisterVehicle stores a vehicle; errors on a blank VehicleId.
	RegisterVehicle(v Vehicle) error
	GetShipment(id string) (Shipment, bool)
	// Vehicles lists all vehicles ordered by VehicleId ascending.
	Vehicles() []Vehicle
	// PlanRoute costs legs for a vehicle; errors on a blank/unknown vehicle id.
	PlanRoute(vehicleId string, legs []RouteLeg) (RoutePlan, error)
}

// InMemoryLogisticsBoard is a concurrency-safe in-memory LogisticsBoard. Ports
// InMemoryLogisticsBoard (shipments + vehicles in maps; an atomic plan sequence).
type InMemoryLogisticsBoard struct {
	mu        sync.RWMutex
	shipments map[string]Shipment
	vehicles  map[string]Vehicle
	seq       int64
}

// NewInMemoryLogisticsBoard constructs an empty board.
func NewInMemoryLogisticsBoard() *InMemoryLogisticsBoard {
	return &InMemoryLogisticsBoard{
		shipments: make(map[string]Shipment),
		vehicles:  make(map[string]Vehicle),
	}
}

// RegisterShipment stores (or replaces by ShipmentId) a shipment. Ports
// RegisterShipment (ArgumentException on blank ShipmentId -> error).
func (b *InMemoryLogisticsBoard) RegisterShipment(s Shipment) error {
	if strings.TrimSpace(s.ShipmentId) == "" {
		return errors.New("ShipmentId required")
	}
	b.mu.Lock()
	b.shipments[s.ShipmentId] = s
	b.mu.Unlock()
	return nil
}

// RegisterVehicle stores (or replaces by VehicleId) a vehicle. Ports
// RegisterVehicle (ArgumentException on blank VehicleId -> error).
func (b *InMemoryLogisticsBoard) RegisterVehicle(v Vehicle) error {
	if strings.TrimSpace(v.VehicleId) == "" {
		return errors.New("VehicleId required")
	}
	b.mu.Lock()
	b.vehicles[v.VehicleId] = v
	b.mu.Unlock()
	return nil
}

// GetShipment returns the shipment for id and true, or (zero, false) if absent.
// Ports GetShipment.
func (b *InMemoryLogisticsBoard) GetShipment(id string) (Shipment, bool) {
	b.mu.RLock()
	s, ok := b.shipments[id]
	b.mu.RUnlock()
	return s, ok
}

// Vehicles lists all vehicles ordered by VehicleId ascending. Ports the Vehicles
// property (OrderBy(VehicleId)).
func (b *InMemoryLogisticsBoard) Vehicles() []Vehicle {
	b.mu.RLock()
	out := make([]Vehicle, 0, len(b.vehicles))
	for _, v := range b.vehicles {
		out = append(out, v)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].VehicleId, out[j].VehicleId) })
	return out
}

// PlanRoute totals the leg distances, costs them at the vehicle's CostPerKm, and
// returns a plan with a fresh sequential id and a defensive copy of the legs.
// Ports PlanRoute (ArgumentException on blank vehicleId, InvalidOperationException
// on unknown vehicle -> errors). Cost mirrors the C# `(decimal)(totalKm*CostPerKm)`.
func (b *InMemoryLogisticsBoard) PlanRoute(vehicleId string, legs []RouteLeg) (RoutePlan, error) {
	if strings.TrimSpace(vehicleId) == "" {
		return RoutePlan{}, errors.New("vehicleId required")
	}
	b.mu.RLock()
	vehicle, ok := b.vehicles[vehicleId]
	b.mu.RUnlock()
	if !ok {
		return RoutePlan{}, errors.New("Unknown vehicle '" + vehicleId + "'.")
	}
	var totalKm float64
	for _, l := range legs {
		totalKm += l.DistanceKm
	}
	cost := DecimalFromFloat(totalKm * vehicle.CostPerKm)
	planId := "plan-" + strconv.FormatInt(atomic.AddInt64(&b.seq, 1), 10)
	return RoutePlan{
		PlanId:          planId,
		VehicleId:       vehicleId,
		Legs:            append([]RouteLeg(nil), legs...),
		TotalDistanceKm: totalKm,
		EstimatedCost:   cost,
	}, nil
}

// Interface guard.
var _ LogisticsBoard = (*InMemoryLogisticsBoard)(nil)
