//! civic — CircleAI civic-board primitives.
//!
//! Full Rust port of `src/CircleAI.Civic/CivicPrimitives.cs`:
//!
//! - Records [`CivicIssue`] / [`Representative`] / [`CivicEvent`], the
//!   [`ICivicBoard`] contract, and the deterministic in-memory
//!   [`InMemoryCivicBoard`] (reported issues + resolution + reps by district +
//!   upcoming events).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Civic) A reported civic issue.
///
/// Mirrors `sealed record CivicIssue(string IssueId, string Category,
/// string Description, double Lat, double Lon, DateTimeOffset ReportedUtc,
/// string Status)`.
#[derive(Debug, Clone, PartialEq)]
pub struct CivicIssue {
    pub issue_id: String,
    pub category: String,
    pub description: String,
    pub lat: f64,
    pub lon: f64,
    pub reported_utc: DateTime<Utc>,
    pub status: String,
}

impl CivicIssue {
    /// Constructs an issue, mirroring the positional C# record constructor.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        issue_id: impl Into<String>,
        category: impl Into<String>,
        description: impl Into<String>,
        lat: f64,
        lon: f64,
        reported_utc: DateTime<Utc>,
        status: impl Into<String>,
    ) -> Self {
        Self {
            issue_id: issue_id.into(),
            category: category.into(),
            description: description.into(),
            lat,
            lon,
            reported_utc,
            status: status.into(),
        }
    }
}

/// (Civic) An elected representative.
///
/// Mirrors `sealed record Representative(string RepId, string Name,
/// string Office, string ContactEmail, string? District)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Representative {
    pub rep_id: String,
    pub name: String,
    pub office: String,
    pub contact_email: String,
    pub district: Option<String>,
}

impl Representative {
    /// Constructs a representative, mirroring the positional C# record constructor.
    pub fn new(
        rep_id: impl Into<String>,
        name: impl Into<String>,
        office: impl Into<String>,
        contact_email: impl Into<String>,
        district: Option<String>,
    ) -> Self {
        Self {
            rep_id: rep_id.into(),
            name: name.into(),
            office: office.into(),
            contact_email: contact_email.into(),
            district,
        }
    }
}

/// (Civic) A civic event.
///
/// Mirrors `sealed record CivicEvent(string EventId, string Title,
/// DateTimeOffset AtUtc, string Location, string Audience)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CivicEvent {
    pub event_id: String,
    pub title: String,
    pub at_utc: DateTime<Utc>,
    pub location: String,
    pub audience: String,
}

impl CivicEvent {
    /// Constructs an event, mirroring the positional C# record constructor.
    pub fn new(
        event_id: impl Into<String>,
        title: impl Into<String>,
        at_utc: DateTime<Utc>,
        location: impl Into<String>,
        audience: impl Into<String>,
    ) -> Self {
        Self {
            event_id: event_id.into(),
            title: title.into(),
            at_utc,
            location: location.into(),
            audience: audience.into(),
        }
    }
}

/// (Civic) The civic-board contract.
///
/// Mirrors `interface ICivicBoard`.
pub trait ICivicBoard {
    /// Reports (or overwrites) an issue.
    fn report(&self, i: CivicIssue);
    /// Sets an issue's status. Panics on an unknown issue id (mirrors the C#
    /// `InvalidOperationException`).
    fn resolve(&self, issue_id: &str, status: &str);
    /// Issues whose status is not "Resolved" (case-insensitive).
    fn open_issues(&self) -> Vec<CivicIssue>;
    /// Adds (or overwrites) a representative.
    fn add_rep(&self, r: Representative);
    /// Representatives for a district (case-insensitive).
    fn reps_for_district(&self, district: &str) -> Vec<Representative>;
    /// Schedules (or overwrites) an event.
    fn schedule(&self, e: CivicEvent);
    /// Events at/after now, earliest first.
    fn upcoming_events(&self) -> Vec<CivicEvent>;
}

/// (Civic) In-memory [`ICivicBoard`].
///
/// Mirrors `sealed class InMemoryCivicBoard`.
pub struct InMemoryCivicBoard {
    issues: Mutex<HashMap<String, CivicIssue>>,
    reps: Mutex<HashMap<String, Representative>>,
    events: Mutex<HashMap<String, CivicEvent>>,
}

impl InMemoryCivicBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            issues: Mutex::new(HashMap::new()),
            reps: Mutex::new(HashMap::new()),
            events: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryCivicBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ICivicBoard for InMemoryCivicBoard {
    fn report(&self, i: CivicIssue) {
        self.issues.lock().unwrap().insert(i.issue_id.clone(), i);
    }

    fn resolve(&self, issue_id: &str, status: &str) {
        let mut issues = self.issues.lock().unwrap();
        match issues.get(issue_id) {
            Some(i) => {
                let updated = CivicIssue {
                    status: status.to_string(),
                    ..i.clone()
                };
                issues.insert(issue_id.to_string(), updated);
            }
            None => panic!("Unknown issue {issue_id}"),
        }
    }

    fn open_issues(&self) -> Vec<CivicIssue> {
        self.issues
            .lock()
            .unwrap()
            .values()
            .filter(|i| !i.status.eq_ignore_ascii_case("Resolved"))
            .cloned()
            .collect()
    }

    fn add_rep(&self, r: Representative) {
        self.reps.lock().unwrap().insert(r.rep_id.clone(), r);
    }

    fn reps_for_district(&self, district: &str) -> Vec<Representative> {
        self.reps
            .lock()
            .unwrap()
            .values()
            .filter(|r| {
                r.district
                    .as_deref()
                    .is_some_and(|d| d.eq_ignore_ascii_case(district))
            })
            .cloned()
            .collect()
    }

    fn schedule(&self, e: CivicEvent) {
        self.events.lock().unwrap().insert(e.event_id.clone(), e);
    }

    fn upcoming_events(&self) -> Vec<CivicEvent> {
        let now = Utc::now();
        let mut hits: Vec<CivicEvent> = self
            .events
            .lock()
            .unwrap()
            .values()
            .filter(|e| e.at_utc >= now)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }
}
