//! parenting_test.rs
//!
//! Ports the behaviour of `CircleAI.Parenting`: children (name-ordered),
//! milestones (newest-first), per-day routines, and age computation.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::parenting::{
    Child, DayOfWeek, IParentingBoard, InMemoryParentingBoard, Milestone, Routine, RoutineEntry,
};

#[test]
fn children_added_and_name_ordered() {
    let board = InMemoryParentingBoard::new();
    assert!(board.get_child("c1").is_none());
    board.add_child(Child::new("c2", "Zara", Utc::now(), None));
    board.add_child(Child::new("c1", "Ben", Utc::now(), Some("M".into())));
    let children = board.children();
    let names: Vec<&str> = children.iter().map(|c| c.name.as_str()).collect();
    assert_eq!(names, vec!["Ben", "Zara"]);
}

#[test]
fn milestones_newest_first() {
    let board = InMemoryParentingBoard::new();
    assert!(board.milestones_for("c1").is_empty());
    board.record_milestone(Milestone::new("m1", "c1", "motor", "crawl", Utc::now() - Duration::days(30)));
    board.record_milestone(Milestone::new("m2", "c1", "speech", "first word", Utc::now()));
    board.record_milestone(Milestone::new("m3", "c2", "motor", "walk", Utc::now()));

    let ms = board.milestones_for("c1");
    let ids: Vec<&str> = ms.iter().map(|m| m.milestone_id.as_str()).collect();
    assert_eq!(ids, vec!["m2", "m1"]);
}

#[test]
#[should_panic(expected = "ChildId required")]
fn record_milestone_blank_child_panics() {
    InMemoryParentingBoard::new()
        .record_milestone(Milestone::new("m1", "  ", "cat", "desc", Utc::now()));
}

#[test]
fn routine_set_and_get_per_day() {
    let board = InMemoryParentingBoard::new();
    let entries = vec![
        RoutineEntry::new("07:00", "Wake"),
        RoutineEntry::new("08:00", "School"),
    ];
    board.set_routine(Routine::new("c1", DayOfWeek::Monday, entries));
    assert!(board.get_routine("c1", DayOfWeek::Tuesday).is_none());
    let mon = board.get_routine("c1", DayOfWeek::Monday).unwrap();
    assert_eq!(mon.entries.len(), 2);
    assert_eq!(mon.day_of_week, DayOfWeek::Monday);
}

#[test]
fn age_as_of_is_difference() {
    let board = InMemoryParentingBoard::new();
    let dob = Utc.with_ymd_and_hms(2020, 1, 1, 0, 0, 0).unwrap();
    board.add_child(Child::new("c1", "Kid", dob, None));
    let at = Utc.with_ymd_and_hms(2021, 1, 1, 0, 0, 0).unwrap();
    assert_eq!(board.age_as_of("c1", at), at - dob);
}

#[test]
#[should_panic(expected = "Unknown child")]
fn age_as_of_unknown_panics() {
    InMemoryParentingBoard::new().age_as_of("nope", Utc::now());
}

#[test]
fn day_of_week_display_matches_csharp() {
    assert_eq!(DayOfWeek::Sunday.to_string(), "Sunday");
    assert_eq!(DayOfWeek::Saturday.to_string(), "Saturday");
}
