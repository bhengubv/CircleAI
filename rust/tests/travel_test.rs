//! travel_test.rs
//!
//! Ports the behaviour of `CircleAI.Travel`: flights + stays + trips +
//! trip-cost aggregation (flights + nightly*nights, nights floored at 1) +
//! upcoming trips.

use chrono::{TimeZone, Utc};
use circle_ai::travel::{Flight, HotelStay, ITravelBoard, InMemoryTravelBoard, TravelTrip};

#[test]
fn trip_cost_sums_flights_and_stays() {
    let board = InMemoryTravelBoard::new();
    let dep = Utc.with_ymd_and_hms(2026, 6, 1, 8, 0, 0).unwrap();
    let arr = Utc.with_ymd_and_hms(2026, 6, 1, 10, 0, 0).unwrap();
    board.add_flight(Flight::new("f1", "JNB", "CPT", dep, arr, "FlySafair", "Economy", 1200.0, "ZAR"));
    let ci = Utc.with_ymd_and_hms(2026, 6, 1, 14, 0, 0).unwrap();
    let co = Utc.with_ymd_and_hms(2026, 6, 4, 14, 0, 0).unwrap(); // exactly 72h after check-in → 3 whole nights
    board.add_stay(HotelStay::new("s1", "Hotel", "CPT", ci, co, 800.0, "ZAR"));

    board.plan(TravelTrip::new("t1", "CPT trip", ci, co, vec!["f1".into()], vec!["s1".into()]));
    // 1200 + 800 * 3 = 3600.
    assert!((board.trip_cost("t1") - 3600.0).abs() < 1e-9);
}

#[test]
fn trip_cost_floors_nights_at_one() {
    let board = InMemoryTravelBoard::new();
    let ci = Utc.with_ymd_and_hms(2026, 6, 1, 14, 0, 0).unwrap();
    // Same-day check-out → (CheckOut - CheckIn).Days == 0 → floored to 1 night.
    let co = Utc.with_ymd_and_hms(2026, 6, 1, 20, 0, 0).unwrap();
    board.add_stay(HotelStay::new("s1", "Hotel", "CPT", ci, co, 500.0, "ZAR"));
    board.plan(TravelTrip::new("t1", "Day", ci, co, vec![], vec!["s1".into()]));
    assert!((board.trip_cost("t1") - 500.0).abs() < 1e-9);
}

#[test]
#[should_panic(expected = "Unknown trip")]
fn trip_cost_unknown_trip_panics() {
    InMemoryTravelBoard::new().trip_cost("nope");
}

#[test]
fn upcoming_trips_ordered() {
    let board = InMemoryTravelBoard::new();
    let now = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    let d = |m| Utc.with_ymd_and_hms(2026, m, 1, 0, 0, 0).unwrap();
    board.plan(TravelTrip::new("t2", "Mar", d(3), d(3), vec![], vec![]));
    board.plan(TravelTrip::new("t1", "Feb", d(2), d(2), vec![], vec![]));
    board.plan(TravelTrip::new("t0", "LastYear", Utc.with_ymd_and_hms(2025, 12, 1, 0, 0, 0).unwrap(), now, vec![], vec![]));

    let up = board.upcoming_trips(now);
    assert_eq!(up.len(), 2);
    assert_eq!(up[0].trip_id, "t1"); // earliest first
    assert_eq!(up[1].trip_id, "t2");
}
