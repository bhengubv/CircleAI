//! real_estate_test.rs
//!
//! Ports the behaviour of `CircleAI.RealEstate`: property registry, listings +
//! close, active-in-suburb (case-insensitive, newest-first) and the
//! suburb-average comparable (None when empty).

use chrono::{Duration, Utc};
use circle_ai::real_estate::{
    IRealEstateBoard, InMemoryRealEstateBoard, Listing, Property, PropertyKind, Valuation, Viewing,
};

fn seed() -> InMemoryRealEstateBoard {
    let board = InMemoryRealEstateBoard::new();
    board.register_property(Property::new("p1", "Sandton", PropertyKind::Apartment, 2, 2, 90.0));
    board.register_property(Property::new("p2", "sandton", PropertyKind::House, 4, 3, 220.0));
    board.register_property(Property::new("p3", "Rosebank", PropertyKind::Townhouse, 3, 2, 150.0));
    board
}

#[test]
fn active_in_suburb_case_insensitive_newest_first() {
    let board = seed();
    let now = Utc::now();
    board.list(Listing::new("l1", "p1", 1_000_000.0, "ZAR", now - Duration::days(2), true));
    board.list(Listing::new("l2", "p2", 3_000_000.0, "ZAR", now, true));
    board.list(Listing::new("l3", "p3", 2_000_000.0, "ZAR", now, true)); // Rosebank
    board.list(Listing::new("l4", "p1", 900_000.0, "ZAR", now - Duration::days(1), false)); // inactive

    let active = board.active_in_suburb("SANDTON");
    let ids: Vec<&str> = active.iter().map(|l| l.listing_id.as_str()).collect();
    assert_eq!(ids, vec!["l2", "l1"]); // newest-first, inactive excluded, Rosebank excluded
}

#[test]
fn close_marks_inactive() {
    let board = seed();
    board.list(Listing::new("l1", "p1", 1_000_000.0, "ZAR", Utc::now(), true));
    board.close("l1");
    assert!(board.active_in_suburb("Sandton").is_empty());
}

#[test]
#[should_panic(expected = "Unknown listing")]
fn close_unknown_panics() {
    InMemoryRealEstateBoard::new().close("nope");
}

#[test]
fn suburb_average_or_none() {
    let board = seed();
    assert!(board.suburb_average("Sandton").is_none());
    board.list(Listing::new("l1", "p1", 1_000_000.0, "ZAR", Utc::now(), true));
    board.list(Listing::new("l2", "p2", 3_000_000.0, "ZAR", Utc::now(), true));
    assert_eq!(board.suburb_average("Sandton"), Some(2_000_000.0));
}

#[test]
fn valuations_and_viewings_accepted() {
    let board = seed();
    board.value(Valuation::new("p1", 1_100_000.0, "AVM", Utc::now()));
    board.list(Listing::new("l1", "p1", 1_000_000.0, "ZAR", Utc::now(), true));
    board.schedule_viewing(Viewing::new("v1", "l1", "Buyer", Utc::now()));
    // No public read for valuations/viewings in the C# contract; exercising the
    // write paths confirms they accept input without panicking.
}
