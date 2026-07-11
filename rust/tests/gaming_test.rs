//! gaming_test.rs
//!
//! Ports the behaviour of `CircleAI.Gaming`: title catalogue + play sessions +
//! total play time + achievements (newest first) + most-played ranking.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::gaming::{
    AchievementUnlock, GameTitle, IGamingBoard, InMemoryGamingBoard, PlaySession,
};

#[test]
fn titles_by_genre_case_insensitive() {
    let board = InMemoryGamingBoard::new();
    board.add_title(GameTitle::new("t1", "Alpha", "RPG", "PC"));
    board.add_title(GameTitle::new("t2", "Beta", "rpg", "Switch"));
    board.add_title(GameTitle::new("t3", "Gamma", "FPS", "PC"));

    assert_eq!(board.titles_by_genre("RPG").len(), 2);
    assert_eq!(board.get_title("t3").unwrap().name, "Gamma");
}

#[test]
fn total_play_time_sums_durations() {
    let board = InMemoryGamingBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record_session(PlaySession::new("s1", "u", "t1", Duration::minutes(30), t));
    board.record_session(PlaySession::new("s2", "u", "t1", Duration::minutes(45), t));
    board.record_session(PlaySession::new("s3", "u", "t2", Duration::minutes(15), t));

    assert_eq!(board.total_play_time("u", "t1"), Duration::minutes(75));
    assert_eq!(board.total_play_time("u", "t2"), Duration::minutes(15));
    assert_eq!(board.total_play_time("u", "none"), Duration::zero());
}

#[test]
fn achievements_newest_first() {
    let board = InMemoryGamingBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.unlock(AchievementUnlock::new("u1", "u", "t1", "First Blood", t));
    board.unlock(AchievementUnlock::new("u2", "u", "t1", "Veteran", t + Duration::hours(3)));

    let a = board.achievements_for("u");
    assert_eq!(a.len(), 2);
    assert_eq!(a[0].unlock_id, "u2"); // newest first
}

#[test]
fn most_played_ranks_by_total_time() {
    let board = InMemoryGamingBoard::new();
    board.add_title(GameTitle::new("t1", "Alpha", "RPG", "PC"));
    board.add_title(GameTitle::new("t2", "Beta", "FPS", "PC"));
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record_session(PlaySession::new("s1", "u", "t1", Duration::minutes(30), t));
    board.record_session(PlaySession::new("s2", "u", "t2", Duration::minutes(120), t));

    let top = board.most_played("u", 5);
    assert_eq!(top.len(), 2);
    assert_eq!(top[0].title_id, "t2"); // most time first
    assert_eq!(top[1].title_id, "t1");

    let top1 = board.most_played("u", 1);
    assert_eq!(top1.len(), 1);
    assert_eq!(top1[0].title_id, "t2");
}

#[test]
#[should_panic(expected = "top_k must be positive")]
fn most_played_zero_topk_panics() {
    InMemoryGamingBoard::new().most_played("u", 0);
}
