//! logistics — CircleAI logistics-board primitives.
//!
//! Full Rust port of `src/CircleAI.Logistics/LogisticsPrimitives.cs`:
//!
//! - Records ([`Shipment`], [`Vehicle`], [`RouteLeg`], [`RoutePlan`]) +
//!   [`ILogisticsBoard`] with the deterministic in-memory
//!   [`InMemoryLogisticsBoard`] (shipment + vehicle registries and a simple
//!   route-cost estimator).
//!
//! `decimal` cost maps to [`f64`]. Plan ids use an atomically-incremented
//! sequence (`plan-{n}`), mirroring the C# `Interlocked.Increment`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering as AtomicOrdering};
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Logistics) A shipment.
///
/// Mirrors `sealed record Shipment(string ShipmentId, string Origin,
/// string Destination, double WeightKg, double VolumeM3, string Incoterm,
/// DateTimeOffset PickupAtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Shipment {
    pub shipment_id: String,
    pub origin: String,
    pub destination: String,
    pub weight_kg: f64,
    pub volume_m3: f64,
    pub incoterm: String,
    pub pickup_at_utc: DateTime<Utc>,
}

impl Shipment {
    /// Constructs a shipment, mirroring the positional C# record constructor.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        shipment_id: impl Into<String>,
        origin: impl Into<String>,
        destination: impl Into<String>,
        weight_kg: f64,
        volume_m3: f64,
        incoterm: impl Into<String>,
        pickup_at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            shipment_id: shipment_id.into(),
            origin: origin.into(),
            destination: destination.into(),
            weight_kg,
            volume_m3,
            incoterm: incoterm.into(),
            pickup_at_utc,
        }
    }
}

/// (Logistics) A vehicle.
///
/// Mirrors `sealed record Vehicle(string VehicleId, double CapacityKg,
/// double CapacityM3, double CostPerKm)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Vehicle {
    pub vehicle_id: String,
    pub capacity_kg: f64,
    pub capacity_m3: f64,
    pub cost_per_km: f64,
}

impl Vehicle {
    /// Constructs a vehicle, mirroring the positional C# record constructor.
    pub fn new(
        vehicle_id: impl Into<String>,
        capacity_kg: f64,
        capacity_m3: f64,
        cost_per_km: f64,
    ) -> Self {
        Self {
            vehicle_id: vehicle_id.into(),
            capacity_kg,
            capacity_m3,
            cost_per_km,
        }
    }
}

/// (Logistics) A single leg of a route.
///
/// Mirrors `sealed record RouteLeg(string FromCode, string ToCode,
/// double DistanceKm)`.
#[derive(Debug, Clone, PartialEq)]
pub struct RouteLeg {
    pub from_code: String,
    pub to_code: String,
    pub distance_km: f64,
}

impl RouteLeg {
    /// Constructs a route leg, mirroring the positional C# record constructor.
    pub fn new(
        from_code: impl Into<String>,
        to_code: impl Into<String>,
        distance_km: f64,
    ) -> Self {
        Self {
            from_code: from_code.into(),
            to_code: to_code.into(),
            distance_km,
        }
    }
}

/// (Logistics) A planned route.
///
/// Mirrors `sealed record RoutePlan(string PlanId, string VehicleId,
/// IReadOnlyList<RouteLeg> Legs, double TotalDistanceKm, decimal EstimatedCost)`.
#[derive(Debug, Clone, PartialEq)]
pub struct RoutePlan {
    pub plan_id: String,
    pub vehicle_id: String,
    pub legs: Vec<RouteLeg>,
    pub total_distance_km: f64,
    pub estimated_cost: f64,
}

impl RoutePlan {
    /// Constructs a route plan, mirroring the positional C# record constructor.
    pub fn new(
        plan_id: impl Into<String>,
        vehicle_id: impl Into<String>,
        legs: Vec<RouteLeg>,
        total_distance_km: f64,
        estimated_cost: f64,
    ) -> Self {
        Self {
            plan_id: plan_id.into(),
            vehicle_id: vehicle_id.into(),
            legs,
            total_distance_km,
            estimated_cost,
        }
    }
}

/// (Logistics) The logistics board contract.
///
/// Mirrors `interface ILogisticsBoard`.
pub trait ILogisticsBoard {
    /// Registers (or overwrites) a shipment. Panics on an empty id.
    fn register_shipment(&self, s: Shipment);
    /// Registers (or overwrites) a vehicle. Panics on an empty id.
    fn register_vehicle(&self, v: Vehicle);
    /// Looks up a shipment by id.
    fn get_shipment(&self, id: &str) -> Option<Shipment>;
    /// All vehicles, ordered by vehicle id ascending.
    fn vehicles(&self) -> Vec<Vehicle>;
    /// Plans a route over `legs` for `vehicle_id`. Panics on an empty vehicle id
    /// or an unknown vehicle (mirrors the C# `ArgumentException` /
    /// `InvalidOperationException`).
    fn plan_route(&self, vehicle_id: &str, legs: Vec<RouteLeg>) -> RoutePlan;
}

/// (Logistics) In-memory [`ILogisticsBoard`].
///
/// Mirrors `sealed class InMemoryLogisticsBoard`.
pub struct InMemoryLogisticsBoard {
    shipments: Mutex<HashMap<String, Shipment>>,
    vehicles: Mutex<HashMap<String, Vehicle>>,
    seq: AtomicI64,
}

impl InMemoryLogisticsBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            shipments: Mutex::new(HashMap::new()),
            vehicles: Mutex::new(HashMap::new()),
            seq: AtomicI64::new(0),
        }
    }
}

impl Default for InMemoryLogisticsBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ILogisticsBoard for InMemoryLogisticsBoard {
    fn register_shipment(&self, s: Shipment) {
        if s.shipment_id.trim().is_empty() {
            panic!("ShipmentId required");
        }
        self.shipments.lock().unwrap().insert(s.shipment_id.clone(), s);
    }

    fn register_vehicle(&self, v: Vehicle) {
        if v.vehicle_id.trim().is_empty() {
            panic!("VehicleId required");
        }
        self.vehicles.lock().unwrap().insert(v.vehicle_id.clone(), v);
    }

    fn get_shipment(&self, id: &str) -> Option<Shipment> {
        self.shipments.lock().unwrap().get(id).cloned()
    }

    fn vehicles(&self) -> Vec<Vehicle> {
        let mut out: Vec<Vehicle> = self.vehicles.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.vehicle_id.cmp(&b.vehicle_id));
        out
    }

    fn plan_route(&self, vehicle_id: &str, legs: Vec<RouteLeg>) -> RoutePlan {
        if vehicle_id.trim().is_empty() {
            panic!("vehicleId required");
        }
        let vehicle = self
            .vehicles
            .lock()
            .unwrap()
            .get(vehicle_id)
            .cloned()
            .unwrap_or_else(|| panic!("Unknown vehicle '{vehicle_id}'."));
        let total_km: f64 = legs.iter().map(|l| l.distance_km).sum();
        let cost = total_km * vehicle.cost_per_km;
        let n = self.seq.fetch_add(1, AtomicOrdering::SeqCst) + 1;
        RoutePlan::new(format!("plan-{n}"), vehicle_id, legs, total_km, cost)
    }
}
