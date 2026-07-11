//! family_test.rs
//!
//! Ports the behaviour of `CircleAI.Family`: member registry (name-ordered),
//! shared events per member (time-ordered), and shared-expense totals by member
//! and by category (since a cutoff).

use chrono::{Duration, Utc};
use circle_ai::family::{
    FamilyEvent, FamilyMember, IFamilyBoard, InMemoryFamilyBoard, SharedExpense,
};

#[test]
fn members_added_and_name_ordered() {
    let board = InMemoryFamilyBoard::new();
    assert!(board.get_member("m1").is_none());
    board.add(FamilyMember::new("m2", "Zed", "Child", Utc::now()));
    board.add(FamilyMember::new("m1", "Amy", "Parent", Utc::now()));
    let members = board.members();
    let names: Vec<&str> = members.iter().map(|m| m.name.as_str()).collect();
    assert_eq!(names, vec!["Amy", "Zed"]);
}

#[test]
fn events_for_member_time_ordered() {
    let board = InMemoryFamilyBoard::new();
    let now = Utc::now();
    board.schedule(FamilyEvent::new("e1", "Dinner", now + Duration::hours(3), vec!["m1".into(), "m2".into()]));
    board.schedule(FamilyEvent::new("e2", "Doctor", now + Duration::hours(1), vec!["m1".into()]));
    board.schedule(FamilyEvent::new("e3", "Soccer", now + Duration::hours(2), vec!["m2".into()]));

    let for_m1 = board.events_for_member("m1");
    let ids: Vec<&str> = for_m1.iter().map(|e| e.event_id.as_str()).collect();
    assert_eq!(ids, vec!["e2", "e1"]); // time-ordered, only events including m1
}

#[test]
fn expense_totals_by_member_and_category() {
    let board = InMemoryFamilyBoard::new();
    let now = Utc::now();
    let since = now - Duration::days(7);
    board.record(SharedExpense::new("x1", "m1", 100.0, "USD", "Groceries", now));
    board.record(SharedExpense::new("x2", "m1", 50.0, "USD", "groceries", now - Duration::days(1)));
    board.record(SharedExpense::new("x3", "m2", 30.0, "USD", "Fuel", now));
    board.record(SharedExpense::new("x4", "m1", 999.0, "USD", "Groceries", now - Duration::days(30))); // before cutoff

    assert_eq!(board.total_paid_by("m1", since), 150.0);
    assert_eq!(board.spend_by_category("GROCERIES", since), 150.0);
    assert_eq!(board.spend_by_category("Fuel", since), 30.0);
}
