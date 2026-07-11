//! faith_test.rs
//!
//! Ports the behaviour of `CircleAI.Faith`: services in a window + recent
//! prayers (newest first) + exact scripture lookup + tradition filter.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::faith::{
    FaithService, IFaithBoard, InMemoryFaithBoard, PrayerRequest, ScriptureReference,
};

#[test]
fn services_between_ordered() {
    let board = InMemoryFaithBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 4, 1, 9, 0, 0).unwrap();
    board.schedule(FaithService::new("s2", "St X", "Evening", t + Duration::hours(9), "Hall"));
    board.schedule(FaithService::new("s1", "St X", "Morning", t, "Hall"));
    board.schedule(FaithService::new("s3", "St X", "NextWeek", t + Duration::days(7), "Hall"));

    let win = board.services_between(t, t + Duration::days(1));
    assert_eq!(win.len(), 2);
    assert_eq!(win[0].service_id, "s1"); // earliest first
}

#[test]
fn recent_prayers_newest_first_and_limited() {
    let board = InMemoryFaithBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.submit_prayer(PrayerRequest::new("p1", "Amy", "one", t, false));
    board.submit_prayer(PrayerRequest::new("p2", "Bob", "two", t + Duration::hours(1), true));

    let recent = board.recent_prayers(20);
    assert_eq!(recent[0].request_id, "p2"); // newest first
    assert_eq!(board.recent_prayers(1).len(), 1);
}

#[test]
fn scripture_lookup_exact_and_tradition_filter() {
    let board = InMemoryFaithBoard::new();
    board.add_scripture(ScriptureReference::new("ref1", "Christian", "John", 3, 16, "For God so loved..."));
    board.add_scripture(ScriptureReference::new("ref2", "Christian", "Psalms", 23, 1, "The Lord is my shepherd"));

    let hit = board.lookup("Christian", "John", 3, 16).unwrap();
    assert_eq!(hit.reference_id, "ref1");
    assert!(board.lookup("Christian", "John", 3, 17).is_none());

    // ByTradition is case-insensitive.
    assert_eq!(board.by_tradition("christian").len(), 2);
}
