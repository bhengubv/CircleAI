//! hospitality_test.rs
//!
//! Ports the behaviour of `CircleAI.Hospitality`: rooms + date availability
//! (clean & unbooked) + reservations + check-out cleaning flag + notes.

use chrono::{TimeZone, Utc};
use circle_ai::hospitality::{
    FrontDeskNote, GuestReservation, HotelRoom, IHospitalityBoard, InMemoryHospitalityBoard,
};

#[test]
fn available_on_excludes_booked_and_dirty() {
    let board = InMemoryHospitalityBoard::new();
    board.add_room(HotelRoom::new("r1", "Single", 900.0, "ZAR", true));
    board.add_room(HotelRoom::new("r2", "Double", 1400.0, "ZAR", true));
    board.add_room(HotelRoom::new("r3", "Suite", 3000.0, "ZAR", false)); // dirty

    let ci = Utc.with_ymd_and_hms(2026, 3, 1, 0, 0, 0).unwrap();
    let co = Utc.with_ymd_and_hms(2026, 3, 5, 0, 0, 0).unwrap();
    board.reserve(GuestReservation::new("res1", "Amy", "r1", ci, co));

    let date = Utc.with_ymd_and_hms(2026, 3, 3, 12, 0, 0).unwrap();
    let avail: Vec<String> = board.available_on(date).into_iter().map(|r| r.room_id).collect();
    assert_eq!(avail, vec!["r2".to_string()]); // r1 booked, r3 dirty
}

#[test]
fn checkout_flags_room_dirty() {
    let board = InMemoryHospitalityBoard::new();
    board.add_room(HotelRoom::new("r1", "Single", 900.0, "ZAR", true));
    let ci = Utc.with_ymd_and_hms(2026, 3, 1, 0, 0, 0).unwrap();
    let co = Utc.with_ymd_and_hms(2026, 3, 5, 0, 0, 0).unwrap();
    board.reserve(GuestReservation::new("res1", "Amy", "r1", ci, co));

    board.check_out("res1", true);
    assert!(!board.get_room("r1").unwrap().is_clean);

    // A date outside the stay: room is dirty so still unavailable.
    let after = Utc.with_ymd_and_hms(2026, 4, 1, 0, 0, 0).unwrap();
    assert!(board.available_on(after).is_empty());
}

#[test]
#[should_panic(expected = "Unknown reservation")]
fn checkout_unknown_reservation_panics() {
    InMemoryHospitalityBoard::new().check_out("nope", false);
}

#[test]
fn notes_newest_first() {
    let board = InMemoryHospitalityBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 3, 1, 8, 0, 0).unwrap();
    board.add_note(FrontDeskNote::new("n1", "res1", "Early check-in", t));
    board.add_note(FrontDeskNote::new("n2", "res1", "Extra towels", t + chrono::Duration::hours(2)));
    let notes = board.notes_for("res1");
    assert_eq!(notes.len(), 2);
    assert_eq!(notes[0].note_id, "n2"); // newest first
}
