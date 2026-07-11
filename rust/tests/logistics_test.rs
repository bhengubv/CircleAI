//! logistics_test.rs
//!
//! Ports the behaviour of `CircleAI.Logistics`: shipment + vehicle registries
//! (vehicles id-ordered), and the route-cost estimator (sum of leg distances ×
//! vehicle cost-per-km, `plan-{n}` ids).

use chrono::Utc;
use circle_ai::logistics::{
    ILogisticsBoard, InMemoryLogisticsBoard, RouteLeg, Shipment, Vehicle,
};

#[test]
fn register_and_query_shipments_and_vehicles() {
    let board = InMemoryLogisticsBoard::new();
    assert!(board.get_shipment("s1").is_none());
    board.register_shipment(Shipment::new("s1", "JHB", "CPT", 100.0, 2.0, "EXW", Utc::now()));
    board.register_vehicle(Vehicle::new("v2", 1000.0, 20.0, 1.5));
    board.register_vehicle(Vehicle::new("v1", 500.0, 10.0, 2.0));

    assert_eq!(board.get_shipment("s1").unwrap().destination, "CPT");
    let vehicles = board.vehicles();
    let ids: Vec<&str> = vehicles.iter().map(|v| v.vehicle_id.as_str()).collect();
    assert_eq!(ids, vec!["v1", "v2"]); // id-ordered ascending
}

#[test]
#[should_panic(expected = "ShipmentId required")]
fn register_shipment_blank_id_panics() {
    InMemoryLogisticsBoard::new()
        .register_shipment(Shipment::new("  ", "A", "B", 1.0, 1.0, "EXW", Utc::now()));
}

#[test]
fn plan_route_sums_distance_and_cost() {
    let board = InMemoryLogisticsBoard::new();
    board.register_vehicle(Vehicle::new("v1", 500.0, 10.0, 2.0));
    let legs = vec![
        RouteLeg::new("JHB", "BFN", 400.0),
        RouteLeg::new("BFN", "CPT", 1000.0),
    ];
    let plan = board.plan_route("v1", legs);
    assert_eq!(plan.plan_id, "plan-1");
    assert_eq!(plan.total_distance_km, 1400.0);
    assert_eq!(plan.estimated_cost, 2800.0);
    assert_eq!(plan.legs.len(), 2);

    // sequence increments.
    let plan2 = board.plan_route("v1", vec![RouteLeg::new("A", "B", 10.0)]);
    assert_eq!(plan2.plan_id, "plan-2");
}

#[test]
#[should_panic(expected = "Unknown vehicle")]
fn plan_route_unknown_vehicle_panics() {
    InMemoryLogisticsBoard::new().plan_route("nope", vec![]);
}
