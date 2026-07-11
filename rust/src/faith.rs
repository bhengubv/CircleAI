//! faith — CircleAI faith-board primitives.
//!
//! Full Rust port of `src/CircleAI.Faith/FaithPrimitives.cs`:
//!
//! - Records [`FaithService`] / [`PrayerRequest`] / [`ScriptureReference`], the
//!   [`IFaithBoard`] contract, and the deterministic in-memory
//!   [`InMemoryFaithBoard`] (services + prayer requests + scripture lookup).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`]. `Lookup` compares
//! tradition/book with the ordinal (case-sensitive) semantics of the C# `==`,
//! while `ByTradition` is case-insensitive, matching the source exactly.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// Default `limit` for [`IFaithBoard::recent_prayers`] (C# `limit = 20`).
pub const DEFAULT_PRAYER_LIMIT: i32 = 20;

/// (Faith) A scheduled service.
///
/// Mirrors `sealed record FaithService(string ServiceId, string CommunityName,
/// string Title, DateTimeOffset StartUtc, string Location)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FaithService {
    pub service_id: String,
    pub community_name: String,
    pub title: String,
    pub start_utc: DateTime<Utc>,
    pub location: String,
}

impl FaithService {
    /// Constructs a service, mirroring the positional C# record constructor.
    pub fn new(
        service_id: impl Into<String>,
        community_name: impl Into<String>,
        title: impl Into<String>,
        start_utc: DateTime<Utc>,
        location: impl Into<String>,
    ) -> Self {
        Self {
            service_id: service_id.into(),
            community_name: community_name.into(),
            title: title.into(),
            start_utc,
            location: location.into(),
        }
    }
}

/// (Faith) A prayer request.
///
/// Mirrors `sealed record PrayerRequest(string RequestId, string Author,
/// string Body, DateTimeOffset SubmittedUtc, bool IsAnonymous)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PrayerRequest {
    pub request_id: String,
    pub author: String,
    pub body: String,
    pub submitted_utc: DateTime<Utc>,
    pub is_anonymous: bool,
}

impl PrayerRequest {
    /// Constructs a request, mirroring the positional C# record constructor.
    pub fn new(
        request_id: impl Into<String>,
        author: impl Into<String>,
        body: impl Into<String>,
        submitted_utc: DateTime<Utc>,
        is_anonymous: bool,
    ) -> Self {
        Self {
            request_id: request_id.into(),
            author: author.into(),
            body: body.into(),
            submitted_utc,
            is_anonymous,
        }
    }
}

/// (Faith) A scripture reference.
///
/// Mirrors `sealed record ScriptureReference(string ReferenceId,
/// string Tradition, string Book, int Chapter, int Verse, string Text)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScriptureReference {
    pub reference_id: String,
    pub tradition: String,
    pub book: String,
    pub chapter: i32,
    pub verse: i32,
    pub text: String,
}

impl ScriptureReference {
    /// Constructs a reference, mirroring the positional C# record constructor.
    pub fn new(
        reference_id: impl Into<String>,
        tradition: impl Into<String>,
        book: impl Into<String>,
        chapter: i32,
        verse: i32,
        text: impl Into<String>,
    ) -> Self {
        Self {
            reference_id: reference_id.into(),
            tradition: tradition.into(),
            book: book.into(),
            chapter,
            verse,
            text: text.into(),
        }
    }
}

/// (Faith) The faith-board contract.
///
/// Mirrors `interface IFaithBoard`.
pub trait IFaithBoard {
    /// Schedules (or overwrites) a service.
    fn schedule(&self, s: FaithService);
    /// Services starting in `[start, end]`, earliest first.
    fn services_between(&self, start: DateTime<Utc>, end: DateTime<Utc>) -> Vec<FaithService>;
    /// Submits a prayer request.
    fn submit_prayer(&self, r: PrayerRequest);
    /// The most-recent prayers, newest first (default [`DEFAULT_PRAYER_LIMIT`]).
    fn recent_prayers(&self, limit: i32) -> Vec<PrayerRequest>;
    /// Adds (or overwrites) a scripture reference.
    fn add_scripture(&self, r: ScriptureReference);
    /// The reference matching `(tradition, book, chapter, verse)` exactly
    /// (tradition/book case-sensitive), if any.
    fn lookup(&self, tradition: &str, book: &str, chapter: i32, verse: i32) -> Option<ScriptureReference>;
    /// References of a tradition (case-insensitive).
    fn by_tradition(&self, tradition: &str) -> Vec<ScriptureReference>;
}

/// (Faith) In-memory [`IFaithBoard`].
///
/// Mirrors `sealed class InMemoryFaithBoard`.
pub struct InMemoryFaithBoard {
    services: Mutex<HashMap<String, FaithService>>,
    prayers: Mutex<Vec<PrayerRequest>>,
    scripture: Mutex<HashMap<String, ScriptureReference>>,
}

impl InMemoryFaithBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            services: Mutex::new(HashMap::new()),
            prayers: Mutex::new(Vec::new()),
            scripture: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryFaithBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IFaithBoard for InMemoryFaithBoard {
    fn schedule(&self, s: FaithService) {
        self.services.lock().unwrap().insert(s.service_id.clone(), s);
    }

    fn services_between(&self, start: DateTime<Utc>, end: DateTime<Utc>) -> Vec<FaithService> {
        let mut hits: Vec<FaithService> = self
            .services
            .lock()
            .unwrap()
            .values()
            .filter(|s| s.start_utc >= start && s.start_utc <= end)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.start_utc.cmp(&b.start_utc));
        hits
    }

    fn submit_prayer(&self, r: PrayerRequest) {
        self.prayers.lock().unwrap().push(r);
    }

    fn recent_prayers(&self, limit: i32) -> Vec<PrayerRequest> {
        let mut hits: Vec<PrayerRequest> = self.prayers.lock().unwrap().clone();
        hits.sort_by(|a, b| b.submitted_utc.cmp(&a.submitted_utc));
        if limit >= 0 {
            hits.truncate(limit as usize);
        }
        hits
    }

    fn add_scripture(&self, r: ScriptureReference) {
        self.scripture.lock().unwrap().insert(r.reference_id.clone(), r);
    }

    fn lookup(&self, tradition: &str, book: &str, chapter: i32, verse: i32) -> Option<ScriptureReference> {
        self.scripture
            .lock()
            .unwrap()
            .values()
            .find(|r| r.tradition == tradition && r.book == book && r.chapter == chapter && r.verse == verse)
            .cloned()
    }

    fn by_tradition(&self, tradition: &str) -> Vec<ScriptureReference> {
        self.scripture
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.tradition.eq_ignore_ascii_case(tradition))
            .cloned()
            .collect()
    }
}
