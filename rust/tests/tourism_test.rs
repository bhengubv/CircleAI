//! tourism_test.rs
//!
//! Ports the behaviour of `CircleAI.Tourism`: attractions by city/tag (ordered
//! by name, case-insensitive) + itineraries + bookings.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::tourism::{
    Attraction, ITourismBoard, InMemoryTourismBoard, Itinerary, ItineraryItem, TourismBooking,
};

#[test]
fn attractions_in_city_ordered_by_name() {
    let board = InMemoryTourismBoard::new();
    board.add(Attraction::new("a1", "Table Mountain", "Cape Town", "ZA", -33.9, 18.4, vec!["nature".into()]));
    board.add(Attraction::new("a2", "Bo-Kaap", "cape town", "ZA", -33.9, 18.4, vec!["culture".into()]));
    board.add(Attraction::new("a3", "Union Buildings", "Pretoria", "ZA", -25.7, 28.2, vec!["history".into()]));

    let ct = board.attractions_in_city("Cape Town");
    assert_eq!(ct.len(), 2);
    assert_eq!(ct[0].name, "Bo-Kaap"); // alphabetical
    assert_eq!(ct[1].name, "Table Mountain");
}

#[test]
fn by_tag_case_insensitive() {
    let board = InMemoryTourismBoard::new();
    board.add(Attraction::new("a1", "Kirstenbosch", "Cape Town", "ZA", 0.0, 0.0, vec!["Nature".into(), "gardens".into()]));
    board.add(Attraction::new("a2", "V&A Waterfront", "Cape Town", "ZA", 0.0, 0.0, vec!["shopping".into()]));

    let nature = board.by_tag("nature");
    assert_eq!(nature.len(), 1);
    assert_eq!(nature[0].attraction_id, "a1");
}

#[test]
#[should_panic(expected = "city required")]
fn blank_city_panics() {
    InMemoryTourismBoard::new().attractions_in_city("");
}

#[test]
fn itineraries_and_bookings() {
    let board = InMemoryTourismBoard::new();
    let items = vec![ItineraryItem::new(0, Duration::hours(9), Duration::hours(12), "a1", Some("morning".into()))];
    board.plan(Itinerary::new("i1", "Weekend", items));
    assert_eq!(board.get_itinerary("i1").unwrap().items.len(), 1);

    let start = Utc.with_ymd_and_hms(2026, 5, 1, 0, 0, 0).unwrap();
    board.book(TourismBooking::new("b1", "i1", start, 2, 1999.0, "ZAR"));
    let bookings = board.bookings();
    assert_eq!(bookings.len(), 1);
    assert!((bookings[0].total_price - 1999.0).abs() < 1e-9);
}
