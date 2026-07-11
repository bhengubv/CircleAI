//! hospitality — CircleAI hospitality-board primitives.
//!
//! Full Rust port of `src/CircleAI.Hospitality/HospitalityPrimitives.cs`:
//!
//! - Records [`HotelRoom`] / [`GuestReservation`] / [`FrontDeskNote`], the
//!   [`IHospitalityBoard`] contract, and the deterministic in-memory
//!   [`InMemoryHospitalityBoard`] (rooms + availability + reservations +
//!   check-out cleaning flag + front-desk notes).
//!
//! Sync-only; `decimal NightlyRate` → `f64`; `DateTime`/`DateTimeOffset` →
//! [`chrono::DateTime<Utc>`].

use std::collections::{HashMap, HashSet};
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Hospitality) A hotel room.
///
/// Mirrors `sealed record HotelRoom(string RoomId, string Type,
/// decimal NightlyRate, string Currency, bool IsClean)`.
#[derive(Debug, Clone, PartialEq)]
pub struct HotelRoom {
    pub room_id: String,
    pub room_type: String,
    pub nightly_rate: f64,
    pub currency: String,
    pub is_clean: bool,
}

impl HotelRoom {
    /// Constructs a room, mirroring the positional C# record constructor. `Type`
    /// is spelled `room_type` (`type` is a Rust keyword).
    pub fn new(
        room_id: impl Into<String>,
        room_type: impl Into<String>,
        nightly_rate: f64,
        currency: impl Into<String>,
        is_clean: bool,
    ) -> Self {
        Self {
            room_id: room_id.into(),
            room_type: room_type.into(),
            nightly_rate,
            currency: currency.into(),
            is_clean,
        }
    }
}

/// (Hospitality) A guest reservation.
///
/// Mirrors `sealed record GuestReservation(string ReservationId,
/// string GuestName, string RoomId, DateTime CheckIn, DateTime CheckOut)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GuestReservation {
    pub reservation_id: String,
    pub guest_name: String,
    pub room_id: String,
    pub check_in: DateTime<Utc>,
    pub check_out: DateTime<Utc>,
}

impl GuestReservation {
    /// Constructs a reservation, mirroring the positional C# record constructor.
    pub fn new(
        reservation_id: impl Into<String>,
        guest_name: impl Into<String>,
        room_id: impl Into<String>,
        check_in: DateTime<Utc>,
        check_out: DateTime<Utc>,
    ) -> Self {
        Self {
            reservation_id: reservation_id.into(),
            guest_name: guest_name.into(),
            room_id: room_id.into(),
            check_in,
            check_out,
        }
    }
}

/// (Hospitality) A front-desk note.
///
/// Mirrors `sealed record FrontDeskNote(string NoteId, string ReservationId,
/// string Body, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FrontDeskNote {
    pub note_id: String,
    pub reservation_id: String,
    pub body: String,
    pub at_utc: DateTime<Utc>,
}

impl FrontDeskNote {
    /// Constructs a note, mirroring the positional C# record constructor.
    pub fn new(
        note_id: impl Into<String>,
        reservation_id: impl Into<String>,
        body: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            note_id: note_id.into(),
            reservation_id: reservation_id.into(),
            body: body.into(),
            at_utc,
        }
    }
}

/// (Hospitality) The hospitality-board contract.
///
/// Mirrors `interface IHospitalityBoard`.
pub trait IHospitalityBoard {
    /// Adds (or overwrites) a room.
    fn add_room(&self, r: HotelRoom);
    /// A room by id, if any.
    fn get_room(&self, id: &str) -> Option<HotelRoom>;
    /// Clean, unbooked rooms on `date` (booked = a reservation spanning the date,
    /// check-in inclusive / check-out exclusive).
    fn available_on(&self, date: DateTime<Utc>) -> Vec<HotelRoom>;
    /// Records (or overwrites) a reservation.
    fn reserve(&self, r: GuestReservation);
    /// Checks a guest out; when `room_needs_cleaning`, flags the room unclean.
    /// Panics on an unknown reservation id (mirrors the C#
    /// `InvalidOperationException`).
    fn check_out(&self, reservation_id: &str, room_needs_cleaning: bool);
    /// A reservation by id, if any.
    fn get_reservation(&self, id: &str) -> Option<GuestReservation>;
    /// Adds a front-desk note.
    fn add_note(&self, n: FrontDeskNote);
    /// Notes for a reservation, newest first.
    fn notes_for(&self, reservation_id: &str) -> Vec<FrontDeskNote>;
}

/// (Hospitality) In-memory [`IHospitalityBoard`].
///
/// Mirrors `sealed class InMemoryHospitalityBoard`.
pub struct InMemoryHospitalityBoard {
    rooms: Mutex<HashMap<String, HotelRoom>>,
    res: Mutex<HashMap<String, GuestReservation>>,
    notes: Mutex<Vec<FrontDeskNote>>,
}

impl InMemoryHospitalityBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            rooms: Mutex::new(HashMap::new()),
            res: Mutex::new(HashMap::new()),
            notes: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryHospitalityBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IHospitalityBoard for InMemoryHospitalityBoard {
    fn add_room(&self, r: HotelRoom) {
        self.rooms.lock().unwrap().insert(r.room_id.clone(), r);
    }

    fn get_room(&self, id: &str) -> Option<HotelRoom> {
        self.rooms.lock().unwrap().get(id).cloned()
    }

    fn available_on(&self, date: DateTime<Utc>) -> Vec<HotelRoom> {
        let booked: HashSet<String> = self
            .res
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.check_in <= date && r.check_out > date)
            .map(|r| r.room_id.clone())
            .collect();
        self.rooms
            .lock()
            .unwrap()
            .values()
            .filter(|r| !booked.contains(&r.room_id) && r.is_clean)
            .cloned()
            .collect()
    }

    fn reserve(&self, r: GuestReservation) {
        self.res.lock().unwrap().insert(r.reservation_id.clone(), r);
    }

    fn check_out(&self, reservation_id: &str, room_needs_cleaning: bool) {
        let res = self.res.lock().unwrap();
        let r = match res.get(reservation_id) {
            Some(r) => r.clone(),
            None => panic!("Unknown reservation {reservation_id}"),
        };
        drop(res);
        if room_needs_cleaning {
            let mut rooms = self.rooms.lock().unwrap();
            if let Some(room) = rooms.get(&r.room_id) {
                let updated = HotelRoom {
                    is_clean: false,
                    ..room.clone()
                };
                rooms.insert(r.room_id.clone(), updated);
            }
        }
    }

    fn get_reservation(&self, id: &str) -> Option<GuestReservation> {
        self.res.lock().unwrap().get(id).cloned()
    }

    fn add_note(&self, n: FrontDeskNote) {
        self.notes.lock().unwrap().push(n);
    }

    fn notes_for(&self, reservation_id: &str) -> Vec<FrontDeskNote> {
        let mut hits: Vec<FrontDeskNote> = self
            .notes
            .lock()
            .unwrap()
            .iter()
            .filter(|n| n.reservation_id == reservation_id)
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        hits
    }
}
