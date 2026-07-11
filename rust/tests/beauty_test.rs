//! beauty_test.rs
//!
//! Ports the behaviour of `CircleAI.Beauty`: treatment catalogue + bookings in
//! a window + skin profiles + concern-based recommendations.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::beauty::{Appointment, IBeautyBoard, InMemoryBeautyBoard, SkinProfile, Treatment};

#[test]
fn appointments_between_ordered() {
    let board = InMemoryBeautyBoard::new();
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 9, 0, 0).unwrap();
    board.book(Appointment::new("a2", "Bob", "t1", base + Duration::hours(2), None));
    board.book(Appointment::new("a1", "Amy", "t1", base, Some("first".into())));
    board.book(Appointment::new("a3", "Cid", "t1", base + Duration::days(2), None)); // outside window

    let win = board.appointments_between(base, base + Duration::hours(3));
    assert_eq!(win.len(), 2);
    assert_eq!(win[0].appt_id, "a1"); // earliest first
    assert_eq!(win[1].appt_id, "a2");
}

#[test]
fn recommend_matches_concerns_case_insensitive() {
    let board = InMemoryBeautyBoard::new();
    board.add_treatment(Treatment::new("t1", "Acne Facial", 60, 450.0, "ZAR"));
    board.add_treatment(Treatment::new("t2", "Relaxing Massage", 90, 700.0, "ZAR"));
    board.save_profile(SkinProfile::new("Amy", "Oily", vec!["acne".into()]));

    let recs = board.recommend_for("Amy");
    assert_eq!(recs.len(), 1);
    assert_eq!(recs[0].treatment_id, "t1");

    // No profile → empty.
    assert!(board.recommend_for("Nobody").is_empty());
}

#[test]
fn get_treatment_and_profile_roundtrip() {
    let board = InMemoryBeautyBoard::new();
    board.add_treatment(Treatment::new("t1", "Peel", 30, 300.0, "ZAR"));
    assert_eq!(board.get_treatment("t1").unwrap().duration_minutes, 30);
    assert!(board.get_treatment("x").is_none());
    board.save_profile(SkinProfile::new("Amy", "Combination", vec![]));
    assert_eq!(board.get_profile("Amy").unwrap().skin_type, "Combination");
}
