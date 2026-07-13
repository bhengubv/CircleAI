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

/// StubGuard parity additions — concrete-only helpers on the in-memory board
/// (mirroring the C# members added to `InMemoryCivicBoard`/`ICivicBoard`).
impl InMemoryCivicBoard {
    /// Number of open (non-"Resolved") issues. Mirrors `OpenIssueCount`.
    pub fn open_issue_count(&self) -> usize {
        self.open_issues().len()
    }

    /// Issues in `category` (case-insensitive), newest first. Mirrors
    /// `IssuesByCategory`.
    pub fn issues_by_category(&self, category: &str) -> Vec<CivicIssue> {
        let mut hits: Vec<CivicIssue> = self
            .issues
            .lock()
            .unwrap()
            .values()
            .filter(|i| i.category.eq_ignore_ascii_case(category))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.reported_utc.cmp(&a.reported_utc));
        hits
    }

    /// Removes a representative by id. Returns `true` if present. Mirrors
    /// `RemoveRep`.
    pub fn remove_rep(&self, rep_id: &str) -> bool {
        self.reps.lock().unwrap().remove(rep_id).is_some()
    }

    /// Representatives holding `office` (case-insensitive), ordered by name
    /// (case-insensitive). Mirrors `RepsForOffice`.
    pub fn reps_for_office(&self, office: &str) -> Vec<Representative> {
        let mut hits: Vec<Representative> = self
            .reps
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.office.eq_ignore_ascii_case(office))
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        hits
    }

    /// Events for `audience` (case-insensitive), earliest first. Mirrors
    /// `EventsForAudience`.
    pub fn events_for_audience(&self, audience: &str) -> Vec<CivicEvent> {
        let mut hits: Vec<CivicEvent> = self
            .events
            .lock()
            .unwrap()
            .values()
            .filter(|e| e.audience.eq_ignore_ascii_case(audience))
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    /// Count of open issues grouped by category (case-insensitive, first-seen
    /// casing kept), ordered by count descending. Mirrors `OpenIssueBreakdown`.
    pub fn open_issue_breakdown(&self) -> Vec<(String, usize)> {
        // Preserve first-seen display casing per case-insensitive category key.
        let mut order: Vec<String> = Vec::new();
        let mut counts: HashMap<String, (String, usize)> = HashMap::new();
        for i in self.open_issues() {
            let key = i.category.to_lowercase();
            match counts.get_mut(&key) {
                Some(entry) => entry.1 += 1,
                None => {
                    order.push(key.clone());
                    counts.insert(key, (i.category.clone(), 1));
                }
            }
        }
        let mut out: Vec<(String, usize)> = order
            .into_iter()
            .map(|k| counts.remove(&k).unwrap())
            .collect();
        out.sort_by(|a, b| b.1.cmp(&a.1));
        out
    }
}
