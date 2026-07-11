//! elderly_test.rs
//!
//! Ports the behaviour of `CircleAI.Elderly`: care plans keyed by resident,
//! medication reminders + deactivation + active filter, check-in log + latest +
//! missed-check-in detection.

use chrono::{Duration, Utc};
use circle_ai::elderly::{
    CarePlan, CheckIn, IElderlyCareBoard, InMemoryElderlyCareBoard, MedReminder,
};

#[test]
fn care_plan_set_and_get() {
    let board = InMemoryElderlyCareBoard::new();
    assert!(board.get_plan("Gran").is_none());
    board.set_plan(CarePlan::new(
        "pl1",
        "Gran",
        vec!["Diabetes".into()],
        vec!["Penicillin".into()],
        "Prefers tea",
    ));
    let plan = board.get_plan("Gran").unwrap();
    assert_eq!(plan.medical_conditions, vec!["Diabetes".to_string()]);
    assert_eq!(plan.carer_notes, "Prefers tea");
}

#[test]
fn reminders_active_filter_and_deactivate() {
    let board = InMemoryElderlyCareBoard::new();
    board.add_reminder(MedReminder::new("r1", "Gran", "Metformin", Duration::hours(8), true));
    board.add_reminder(MedReminder::new("r2", "Gran", "Aspirin", Duration::hours(20), true));
    board.add_reminder(MedReminder::new("r3", "Pops", "Statin", Duration::hours(21), true));
    assert_eq!(board.active_reminders_for("Gran").len(), 2);

    board.deactivate_reminder("r1");
    let active = board.active_reminders_for("Gran");
    assert_eq!(active.len(), 1);
    assert_eq!(active[0].reminder_id, "r2");
}

#[test]
#[should_panic(expected = "Unknown reminder")]
fn deactivate_unknown_reminder_panics() {
    InMemoryElderlyCareBoard::new().deactivate_reminder("nope");
}

#[test]
fn latest_check_in_and_missed_detection() {
    let board = InMemoryElderlyCareBoard::new();
    let now = Utc::now();
    // No check-in yet → missed.
    assert!(board.latest_check_in("Gran").is_none());
    assert!(board.missed_check_in("Gran", now - Duration::hours(1)));

    board.record_check_in(CheckIn::new("c1", "Gran", now - Duration::hours(3), "OK", None));
    board.record_check_in(CheckIn::new("c2", "Gran", now, "OK", Some("all good".into())));

    assert_eq!(board.latest_check_in("Gran").unwrap().check_in_id, "c2");
    // latest is `now`, which is >= (now - 1h) → not missed.
    assert!(!board.missed_check_in("Gran", now - Duration::hours(1)));
    // require a check-in in the future → missed.
    assert!(board.missed_check_in("Gran", now + Duration::hours(1)));
}
