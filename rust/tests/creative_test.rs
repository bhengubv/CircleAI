//! creative_test.rs
//!
//! Ports the behaviour of `CircleAI.Creative`: works by tag + recent
//! inspiration (newest first) + critiques + average score (0 when none).

use chrono::{Duration, TimeZone, Utc};
use circle_ai::creative::{
    CreativeWork, Critique, ICreativeBoard, InMemoryCreativeBoard, Inspiration,
};

#[test]
fn works_by_tag_case_insensitive() {
    let board = InMemoryCreativeBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.add_work(CreativeWork::new("w1", "Sunset", "Painting", "Amy", t, vec!["Landscape".into()]));
    board.add_work(CreativeWork::new("w2", "Portrait", "Painting", "Bob", t, vec!["figure".into()]));

    let hits = board.works_by_tag("landscape");
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].work_id, "w1");
    assert_eq!(board.get_work("w2").unwrap().author, "Bob");
}

#[test]
fn recent_inspiration_newest_first_and_limited() {
    let board = InMemoryCreativeBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record_inspiration(Inspiration::new("i1", "prompt A", "http://a", t));
    board.record_inspiration(Inspiration::new("i2", "prompt B", "http://b", t + Duration::hours(1)));

    let recent = board.recent_inspiration(20);
    assert_eq!(recent[0].inspiration_id, "i2"); // newest first
    assert_eq!(board.recent_inspiration(1).len(), 1);
}

#[test]
fn avg_score_over_critiques() {
    let board = InMemoryCreativeBoard::new();
    board.add_critique(Critique::new("c1", "w1", "Amy", "Good", 8));
    board.add_critique(Critique::new("c2", "w1", "Bob", "Great", 10));
    board.add_critique(Critique::new("c3", "w2", "Cid", "Meh", 4));

    assert!((board.avg_score("w1") - 9.0).abs() < 1e-9);
    // No critiques → 0.
    assert!((board.avg_score("w9") - 0.0).abs() < 1e-9);
}
