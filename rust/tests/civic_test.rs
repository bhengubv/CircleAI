//! civic_test.rs
//!
//! Ports the behaviour of `CircleAI.Civic`: issues + resolution + open filter +
//! reps by district + upcoming events.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::civic::{CivicEvent, CivicIssue, ICivicBoard, InMemoryCivicBoard, Representative};

#[test]
fn open_issues_excludes_resolved() {
    let board = InMemoryCivicBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.report(CivicIssue::new("i1", "Roads", "Pothole", -26.2, 28.0, t, "Open"));
    board.report(CivicIssue::new("i2", "Water", "Leak", -26.2, 28.0, t, "Open"));
    board.resolve("i1", "Resolved");

    let open = board.open_issues();
    assert_eq!(open.len(), 1);
    assert_eq!(open[0].issue_id, "i2");
}

#[test]
#[should_panic(expected = "Unknown issue")]
fn resolve_unknown_panics() {
    InMemoryCivicBoard::new().resolve("nope", "Resolved");
}

#[test]
fn reps_for_district_case_insensitive() {
    let board = InMemoryCivicBoard::new();
    board.add_rep(Representative::new("r1", "Alice", "Ward 5", "a@x.gov", Some("Ward 5".into())));
    board.add_rep(Representative::new("r2", "Bob", "Ward 6", "b@x.gov", Some("Ward 6".into())));
    board.add_rep(Representative::new("r3", "Cid", "At Large", "c@x.gov", None));

    let w5 = board.reps_for_district("ward 5");
    assert_eq!(w5.len(), 1);
    assert_eq!(w5[0].rep_id, "r1");
}

#[test]
fn upcoming_events_future_only_ordered() {
    let board = InMemoryCivicBoard::new();
    let far = Utc::now() + Duration::days(30);
    board.schedule(CivicEvent::new("e2", "Later", far + Duration::days(1), "Hall", "Public"));
    board.schedule(CivicEvent::new("e1", "Soon", far, "Hall", "Public"));
    board.schedule(CivicEvent::new("e0", "Past", Utc::now() - Duration::days(1), "Hall", "Public"));

    let up = board.upcoming_events();
    assert_eq!(up.len(), 2);
    assert_eq!(up[0].event_id, "e1"); // earliest future first
}
