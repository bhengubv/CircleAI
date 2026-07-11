//! sports_test.rs
//!
//! Ports the behaviour of `CircleAI.Sports`: activity log + history ordering +
//! weekly volume + fastest-effort personal best + scheduled/completed sessions.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::sports::{
    Activity, DistanceKind, ISportsBoard, InMemorySportsBoard, TrainingSession,
};

#[test]
fn history_newest_first_and_limited() {
    let board = InMemorySportsBoard::new();
    let t0 = Utc.with_ymd_and_hms(2026, 1, 1, 8, 0, 0).unwrap();
    board.log(Activity::new("a1", "u", DistanceKind::Run, 5.0, Duration::minutes(30), t0));
    board.log(Activity::new("a2", "u", DistanceKind::Run, 10.0, Duration::minutes(60), t0 + Duration::hours(1)));
    board.log(Activity::new("a3", "other", DistanceKind::Run, 3.0, Duration::minutes(20), t0));

    let h = board.history("u", 50);
    assert_eq!(h.len(), 2);
    assert_eq!(h[0].activity_id, "a2"); // newest first
    assert_eq!(h[1].activity_id, "a1");

    let limited = board.history("u", 1);
    assert_eq!(limited.len(), 1);
    assert_eq!(limited[0].activity_id, "a2");
}

#[test]
#[should_panic(expected = "limit must be positive")]
fn history_zero_limit_panics() {
    InMemorySportsBoard::new().history("u", 0);
}

#[test]
fn total_km_this_week_sums_matching_kind() {
    let board = InMemorySportsBoard::new();
    // now = Wednesday 2026-01-07; week starts Sunday 2026-01-04.
    let now = Utc.with_ymd_and_hms(2026, 1, 7, 12, 0, 0).unwrap();
    board.log(Activity::new("a1", "u", DistanceKind::Run, 5.0, Duration::minutes(30), Utc.with_ymd_and_hms(2026, 1, 5, 8, 0, 0).unwrap()));
    board.log(Activity::new("a2", "u", DistanceKind::Run, 7.0, Duration::minutes(45), now));
    // Before the week start — excluded.
    board.log(Activity::new("a3", "u", DistanceKind::Run, 100.0, Duration::minutes(600), Utc.with_ymd_and_hms(2026, 1, 3, 8, 0, 0).unwrap()));
    // Different kind — excluded.
    board.log(Activity::new("a4", "u", DistanceKind::Bike, 50.0, Duration::minutes(90), now));

    assert!((board.total_km_this_week("u", DistanceKind::Run, now) - 12.0).abs() < 1e-9);
}

#[test]
fn best_is_fastest_qualifying_effort() {
    let board = InMemorySportsBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 8, 0, 0).unwrap();
    board.log(Activity::new("a1", "u", DistanceKind::Run, 10.0, Duration::minutes(60), t));
    board.log(Activity::new("a2", "u", DistanceKind::Run, 12.0, Duration::minutes(50), t)); // faster & further
    board.log(Activity::new("a3", "u", DistanceKind::Run, 4.0, Duration::minutes(15), t)); // too short

    let best = board.best("u", DistanceKind::Run, 10.0).unwrap();
    assert_eq!(best.time, Duration::minutes(50));
    assert!(board.best("u", DistanceKind::Run, 20.0).is_none());
}

#[test]
fn schedule_complete_and_upcoming() {
    let board = InMemorySportsBoard::new();
    let future = Utc::now() + Duration::days(2);
    board.schedule(TrainingSession::new("s1", "u", "Tempo", future, false));
    board.schedule(TrainingSession::new("s2", "u", "Long", future + Duration::days(1), false));
    // Past session — never upcoming.
    board.schedule(TrainingSession::new("s3", "u", "Old", Utc::now() - Duration::days(1), false));

    let up = board.upcoming("u");
    assert_eq!(up.len(), 2);
    assert_eq!(up[0].session_id, "s1"); // earliest first

    board.complete("s1");
    let up2 = board.upcoming("u");
    assert_eq!(up2.len(), 1);
    assert_eq!(up2[0].session_id, "s2");
}

#[test]
#[should_panic(expected = "Unknown session")]
fn complete_unknown_session_panics() {
    InMemorySportsBoard::new().complete("nope");
}
