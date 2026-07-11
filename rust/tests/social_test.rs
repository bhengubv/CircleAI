//! social_test.rs
//!
//! Ports the behaviour of `CircleAI.Social`: posts + case-insensitive reaction
//! counts + follow graph (no self-follow) + feed by followed authors + followers.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::social::{Follow, ISocialBoard, InMemorySocialBoard, Reaction, SocialPost};

#[test]
fn reaction_count_case_insensitive() {
    let board = InMemorySocialBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.react(Reaction::new("p1", "u1", "Like", t));
    board.react(Reaction::new("p1", "u2", "like", t));
    board.react(Reaction::new("p1", "u3", "love", t));

    assert_eq!(board.reaction_count("p1", "LIKE"), 2);
    assert_eq!(board.reaction_count("p1", "love"), 1);
    assert_eq!(board.reaction_count("p1", "wow"), 0);
}

#[test]
#[should_panic(expected = "Cannot follow yourself")]
fn self_follow_panics() {
    let board = InMemorySocialBoard::new();
    board.follow(Follow::new("u1", "u1", Utc::now()));
}

#[test]
fn feed_shows_followed_authors_newest_first() {
    let board = InMemorySocialBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.follow(Follow::new("me", "alice", t));
    board.post(SocialPost::new("p1", "alice", "hello", t, vec![]));
    board.post(SocialPost::new("p2", "alice", "again", t + Duration::hours(1), vec![]));
    board.post(SocialPost::new("p3", "bob", "unfollowed", t, vec![])); // not followed

    let feed = board.feed_for("me", 20);
    assert_eq!(feed.len(), 2);
    assert_eq!(feed[0].post_id, "p2"); // newest first
}

#[test]
fn unfollow_removes_from_feed_and_followers() {
    let board = InMemorySocialBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.follow(Follow::new("me", "alice", t));
    board.post(SocialPost::new("p1", "alice", "hi", t, vec![]));
    assert_eq!(board.followers("alice"), vec!["me".to_string()]);

    board.unfollow("me", "alice");
    assert!(board.feed_for("me", 20).is_empty());
    assert!(board.followers("alice").is_empty());
}

#[test]
#[should_panic(expected = "limit must be positive")]
fn feed_zero_limit_panics() {
    InMemorySocialBoard::new().feed_for("me", 0);
}
