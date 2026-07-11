//! kids_test.rs
//!
//! Ports the behaviour of `CircleAI.Kids`: age-banded content + per-child
//! limits + same-day usage totals + over-limit detection.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::kids::{AgeAppropriateness, DailyTime, IKidsBoard, InMemoryKidsBoard, KidsContent, TimeLog};

#[test]
fn content_for_band_ordered_by_title() {
    let board = InMemoryKidsBoard::new();
    board.add_content(KidsContent::new("c2", "Zebra Facts", AgeAppropriateness::EarlyPrimary, "video", vec![]));
    board.add_content(KidsContent::new("c1", "ABC Song", AgeAppropriateness::EarlyPrimary, "video", vec![]));
    board.add_content(KidsContent::new("c3", "Teen Drama", AgeAppropriateness::Teen, "video", vec![]));

    let ep = board.content_for(AgeAppropriateness::EarlyPrimary);
    assert_eq!(ep.len(), 2);
    assert_eq!(ep[0].title, "ABC Song"); // alphabetical
    assert_eq!(ep[1].title, "Zebra Facts");
}

#[test]
fn used_today_sums_same_date_only() {
    let board = InMemoryKidsBoard::new();
    let now = Utc.with_ymd_and_hms(2026, 2, 10, 14, 0, 0).unwrap();
    board.record_time(TimeLog::new("Kid", "screen", Duration::minutes(30), Utc.with_ymd_and_hms(2026, 2, 10, 9, 0, 0).unwrap()));
    board.record_time(TimeLog::new("Kid", "screen", Duration::minutes(20), now));
    // Yesterday — excluded.
    board.record_time(TimeLog::new("Kid", "screen", Duration::minutes(99), Utc.with_ymd_and_hms(2026, 2, 9, 9, 0, 0).unwrap()));

    assert_eq!(board.used_today("Kid", "screen", now), Duration::minutes(50));
}

#[test]
fn over_limit_respects_caps() {
    let board = InMemoryKidsBoard::new();
    let now = Utc.with_ymd_and_hms(2026, 2, 10, 14, 0, 0).unwrap();
    board.set_limits(DailyTime::new("Kid", Duration::minutes(60), Duration::minutes(30)));
    board.record_time(TimeLog::new("Kid", "screen", Duration::minutes(90), now));

    assert!(board.over_limit("Kid", "screen", now)); // 90 > 60
    assert!(!board.over_limit("Kid", "reading", now)); // 0 reading used
    // Uncapped kind never over limit.
    board.record_time(TimeLog::new("Kid", "outdoor", Duration::minutes(500), now));
    assert!(!board.over_limit("Kid", "outdoor", now));
    // No limits set for another child → false.
    assert!(!board.over_limit("Other", "screen", now));
}
