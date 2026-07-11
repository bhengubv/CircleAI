//! relationships_test.rs
//!
//! Ports the behaviour of `CircleAI.Relationships`: contacts (by name) +
//! important dates this month + last-touchpoint + stale-contact detection.

use chrono::{Datelike, Duration, TimeZone, Utc};
use circle_ai::relationships::{
    ContactEvent, IRelationshipsBoard, ImportantDate, InMemoryRelationshipsBoard, PersonContact,
};

#[test]
fn contacts_sorted_by_name() {
    let board = InMemoryRelationshipsBoard::new();
    board.add_contact(PersonContact::new("c2", "Zoe", "Friend", None));
    board.add_contact(PersonContact::new("c1", "Amy", "Sister", Some("birthday soon".into())));

    let cs = board.contacts();
    assert_eq!(cs.len(), 2);
    assert_eq!(cs[0].name, "Amy");
    assert_eq!(cs[1].name, "Zoe");
    assert_eq!(board.get_contact("c1").unwrap().relationship, "Sister");
}

#[test]
fn upcoming_this_month_filters_by_current_month() {
    let board = InMemoryRelationshipsBoard::new();
    let now = Utc::now();
    let this_month = Utc
        .with_ymd_and_hms(now.year(), now.month(), 15, 0, 0, 0)
        .single()
        .unwrap_or(now);
    // A date in a clearly different month (offset by 6 months).
    let other_month = this_month + Duration::days(183);

    board.add_important_date(ImportantDate::new("d1", "c1", "Birthday", this_month));
    board.add_important_date(ImportantDate::new("d2", "c2", "Anniversary", other_month));

    let up = board.upcoming_this_month();
    assert!(up.iter().any(|d| d.date_id == "d1"));
    assert!(!up.iter().any(|d| d.date_id == "d2"));
}

#[test]
fn last_contact_and_not_contacted_since() {
    let board = InMemoryRelationshipsBoard::new();
    board.add_contact(PersonContact::new("c1", "Amy", "Friend", None));
    board.add_contact(PersonContact::new("c2", "Bob", "Friend", None));

    let t0 = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record_touchpoint(ContactEvent::new("c1", "call", t0, None));
    board.record_touchpoint(ContactEvent::new("c1", "text", t0 + Duration::days(5), None));

    assert_eq!(board.last_contact("c1"), Some(t0 + Duration::days(5)));
    assert!(board.last_contact("c2").is_none());

    // cutoff after c1's last touch → c1 is stale; c2 (never contacted) always stale.
    let cutoff = t0 + Duration::days(10);
    let stale: Vec<String> = board.not_contacted_since(cutoff).into_iter().map(|c| c.contact_id).collect();
    assert!(stale.contains(&"c1".to_string()));
    assert!(stale.contains(&"c2".to_string()));

    // cutoff before c1's last touch → c1 not stale.
    let cutoff2 = t0 + Duration::days(3);
    let stale2: Vec<String> = board.not_contacted_since(cutoff2).into_iter().map(|c| c.contact_id).collect();
    assert!(!stale2.contains(&"c1".to_string()));
    assert!(stale2.contains(&"c2".to_string()));
}
