//! community_test.rs
//!
//! Ports the behaviour of `CircleAI.Community`: groups + membership lookup +
//! announcements (newest first, limited) + future volunteer opportunities.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::community::{
    Announcement, CommunityGroup, ICommunityBoard, InMemoryCommunityBoard, VolunteerOpportunity,
};

#[test]
fn groups_for_member() {
    let board = InMemoryCommunityBoard::new();
    board.create(CommunityGroup::new("g1", "Runners", "Fitness", vec!["u1".into(), "u2".into()]));
    board.create(CommunityGroup::new("g2", "Readers", "Books", vec!["u2".into()]));
    board.create(CommunityGroup::new("g3", "Gamers", "Play", vec!["u3".into()]));

    assert_eq!(board.groups_for_member("u2").len(), 2);
    assert_eq!(board.get_group("g1").unwrap().name, "Runners");
}

#[test]
fn announcements_newest_first_and_limited() {
    let board = InMemoryCommunityBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.post(Announcement::new("a1", "g1", "One", "b", t));
    board.post(Announcement::new("a2", "g1", "Two", "b", t + Duration::hours(1)));
    board.post(Announcement::new("a3", "g1", "Three", "b", t + Duration::hours(2)));
    board.post(Announcement::new("a4", "g2", "Other", "b", t + Duration::hours(3)));

    let all = board.announcements_for("g1", 20);
    assert_eq!(all.len(), 3);
    assert_eq!(all[0].announcement_id, "a3"); // newest first

    let limited = board.announcements_for("g1", 2);
    assert_eq!(limited.len(), 2);
    assert_eq!(limited[0].announcement_id, "a3");
}

#[test]
fn opportunities_future_only_ordered() {
    let board = InMemoryCommunityBoard::new();
    let far = Utc::now() + Duration::days(10);
    board.list(VolunteerOpportunity::new("o2", "g1", "Later", 3, far + Duration::days(1)));
    board.list(VolunteerOpportunity::new("o1", "g1", "Soon", 5, far));
    board.list(VolunteerOpportunity::new("o0", "g1", "Past", 2, Utc::now() - Duration::days(1)));

    let opps = board.opportunities();
    assert_eq!(opps.len(), 2);
    assert_eq!(opps[0].opp_id, "o1"); // earliest future first
}
