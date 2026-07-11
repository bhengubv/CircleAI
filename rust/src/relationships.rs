//! relationships — CircleAI relationships-board primitives.
//!
//! Full Rust port of `src/CircleAI.Relationships/RelationshipsPrimitives.cs`:
//!
//! - Records [`PersonContact`] / [`ImportantDate`] / [`ContactEvent`], the
//!   [`IRelationshipsBoard`] contract, and the deterministic in-memory
//!   [`InMemoryRelationshipsBoard`] (contacts + important dates this month +
//!   last-touchpoint tracking + stale-contact detection).
//!
//! Sync-only; `DateTime`/`DateTimeOffset` → [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Datelike, Utc};

/// (Relationships) A personal contact.
///
/// Mirrors `sealed record PersonContact(string ContactId, string Name,
/// string Relationship, string? Notes)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PersonContact {
    pub contact_id: String,
    pub name: String,
    pub relationship: String,
    pub notes: Option<String>,
}

impl PersonContact {
    /// Constructs a contact, mirroring the positional C# record constructor.
    pub fn new(
        contact_id: impl Into<String>,
        name: impl Into<String>,
        relationship: impl Into<String>,
        notes: Option<String>,
    ) -> Self {
        Self {
            contact_id: contact_id.into(),
            name: name.into(),
            relationship: relationship.into(),
            notes,
        }
    }
}

/// (Relationships) An important date for a contact.
///
/// Mirrors `sealed record ImportantDate(string DateId, string ContactId,
/// string Kind, DateTime Date)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ImportantDate {
    pub date_id: String,
    pub contact_id: String,
    pub kind: String,
    pub date: DateTime<Utc>,
}

impl ImportantDate {
    /// Constructs an important date, mirroring the positional C# record constructor.
    pub fn new(
        date_id: impl Into<String>,
        contact_id: impl Into<String>,
        kind: impl Into<String>,
        date: DateTime<Utc>,
    ) -> Self {
        Self {
            date_id: date_id.into(),
            contact_id: contact_id.into(),
            kind: kind.into(),
            date,
        }
    }
}

/// (Relationships) A recorded touchpoint with a contact.
///
/// Mirrors `sealed record ContactEvent(string ContactId, string Kind,
/// DateTimeOffset AtUtc, string? Note)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContactEvent {
    pub contact_id: String,
    pub kind: String,
    pub at_utc: DateTime<Utc>,
    pub note: Option<String>,
}

impl ContactEvent {
    /// Constructs a touchpoint, mirroring the positional C# record constructor.
    pub fn new(
        contact_id: impl Into<String>,
        kind: impl Into<String>,
        at_utc: DateTime<Utc>,
        note: Option<String>,
    ) -> Self {
        Self {
            contact_id: contact_id.into(),
            kind: kind.into(),
            at_utc,
            note,
        }
    }
}

/// (Relationships) The relationships-board contract.
///
/// Mirrors `interface IRelationshipsBoard`.
pub trait IRelationshipsBoard {
    /// Adds (or overwrites) a contact.
    fn add_contact(&self, c: PersonContact);
    /// A contact by id, if any.
    fn get_contact(&self, id: &str) -> Option<PersonContact>;
    /// All contacts, by name (mirrors the C# `Contacts` property).
    fn contacts(&self) -> Vec<PersonContact>;
    /// Adds (or overwrites) an important date.
    fn add_important_date(&self, d: ImportantDate);
    /// Important dates falling in the current UTC month, by day-of-month.
    fn upcoming_this_month(&self) -> Vec<ImportantDate>;
    /// Records a touchpoint.
    fn record_touchpoint(&self, e: ContactEvent);
    /// The most-recent touchpoint time for a contact, if any.
    fn last_contact(&self, contact_id: &str) -> Option<DateTime<Utc>>;
    /// Contacts whose last touchpoint is before `cutoff` (or who have none).
    fn not_contacted_since(&self, cutoff: DateTime<Utc>) -> Vec<PersonContact>;
}

/// (Relationships) In-memory [`IRelationshipsBoard`].
///
/// Mirrors `sealed class InMemoryRelationshipsBoard`.
pub struct InMemoryRelationshipsBoard {
    contacts: Mutex<HashMap<String, PersonContact>>,
    dates: Mutex<HashMap<String, ImportantDate>>,
    events: Mutex<Vec<ContactEvent>>,
}

impl InMemoryRelationshipsBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            contacts: Mutex::new(HashMap::new()),
            dates: Mutex::new(HashMap::new()),
            events: Mutex::new(Vec::new()),
        }
    }

    /// Internal: latest touchpoint time without re-locking `events` per contact.
    fn last_contact_locked(events: &[ContactEvent], contact_id: &str) -> Option<DateTime<Utc>> {
        let mut best: Option<DateTime<Utc>> = None;
        for e in events.iter().filter(|e| e.contact_id == contact_id) {
            match best {
                Some(b) if e.at_utc > b => best = Some(e.at_utc),
                None => best = Some(e.at_utc),
                _ => {}
            }
        }
        best
    }
}

impl Default for InMemoryRelationshipsBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IRelationshipsBoard for InMemoryRelationshipsBoard {
    fn add_contact(&self, c: PersonContact) {
        self.contacts.lock().unwrap().insert(c.contact_id.clone(), c);
    }

    fn get_contact(&self, id: &str) -> Option<PersonContact> {
        self.contacts.lock().unwrap().get(id).cloned()
    }

    fn contacts(&self) -> Vec<PersonContact> {
        let mut hits: Vec<PersonContact> = self.contacts.lock().unwrap().values().cloned().collect();
        hits.sort_by(|a, b| a.name.cmp(&b.name));
        hits
    }

    fn add_important_date(&self, d: ImportantDate) {
        self.dates.lock().unwrap().insert(d.date_id.clone(), d);
    }

    fn upcoming_this_month(&self) -> Vec<ImportantDate> {
        let month = Utc::now().month();
        let mut hits: Vec<ImportantDate> = self
            .dates
            .lock()
            .unwrap()
            .values()
            .filter(|d| d.date.month() == month)
            .cloned()
            .collect();
        hits.sort_by_key(|d| d.date.day());
        hits
    }

    fn record_touchpoint(&self, e: ContactEvent) {
        self.events.lock().unwrap().push(e);
    }

    fn last_contact(&self, contact_id: &str) -> Option<DateTime<Utc>> {
        let events = self.events.lock().unwrap();
        Self::last_contact_locked(&events, contact_id)
    }

    fn not_contacted_since(&self, cutoff: DateTime<Utc>) -> Vec<PersonContact> {
        let events = self.events.lock().unwrap();
        self.contacts
            .lock()
            .unwrap()
            .values()
            .filter(|c| match Self::last_contact_locked(&events, &c.contact_id) {
                None => true,
                Some(last) => last < cutoff,
            })
            .cloned()
            .collect()
    }
}
