//! sports — CircleAI sports-board primitives.
//!
//! Full Rust port of `src/CircleAI.Sports/SportsPrimitives.cs`:
//!
//! - [`DistanceKind`] enum, records [`Activity`] / [`PersonalBest`] /
//!   [`TrainingSession`], the [`ISportsBoard`] contract, and the deterministic
//!   in-memory [`InMemorySportsBoard`] (activity log + weekly volume + best +
//!   scheduled sessions).
//!
//! Sync-only; `TimeSpan Duration` → [`chrono::Duration`]; `DateTimeOffset` →
//! [`chrono::DateTime<Utc>`]. `Activity` is re-exported at the crate root as
//! `SportsActivity` to avoid clashing with `crm::Activity`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Datelike, Duration, Utc};

/// (Sports) Kind of distance-based activity.
///
/// Mirrors `enum DistanceKind { Run, Bike, Swim, Walk, Row }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DistanceKind {
    Run,
    Bike,
    Swim,
    Walk,
    Row,
}

/// (Sports) A logged activity.
///
/// Mirrors `sealed record Activity(string ActivityId, string UserId,
/// DistanceKind Kind, double DistanceKm, TimeSpan Duration, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Activity {
    pub activity_id: String,
    pub user_id: String,
    pub kind: DistanceKind,
    pub distance_km: f64,
    pub duration: Duration,
    pub at_utc: DateTime<Utc>,
}

impl Activity {
    /// Constructs an activity, mirroring the positional C# record constructor.
    pub fn new(
        activity_id: impl Into<String>,
        user_id: impl Into<String>,
        kind: DistanceKind,
        distance_km: f64,
        duration: Duration,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            activity_id: activity_id.into(),
            user_id: user_id.into(),
            kind,
            distance_km,
            duration,
            at_utc,
        }
    }
}

/// (Sports) A personal best over a given distance.
///
/// Mirrors `sealed record PersonalBest(string UserId, DistanceKind Kind,
/// double DistanceKm, TimeSpan Time, DateTimeOffset AchievedUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct PersonalBest {
    pub user_id: String,
    pub kind: DistanceKind,
    pub distance_km: f64,
    pub time: Duration,
    pub achieved_utc: DateTime<Utc>,
}

impl PersonalBest {
    /// Constructs a personal best, mirroring the positional C# record constructor.
    pub fn new(
        user_id: impl Into<String>,
        kind: DistanceKind,
        distance_km: f64,
        time: Duration,
        achieved_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            user_id: user_id.into(),
            kind,
            distance_km,
            time,
            achieved_utc,
        }
    }
}

/// (Sports) A scheduled training session.
///
/// Mirrors `sealed record TrainingSession(string SessionId, string UserId,
/// string Plan, DateTimeOffset ScheduledUtc, bool Completed)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TrainingSession {
    pub session_id: String,
    pub user_id: String,
    pub plan: String,
    pub scheduled_utc: DateTime<Utc>,
    pub completed: bool,
}

impl TrainingSession {
    /// Constructs a training session, mirroring the positional C# record constructor.
    pub fn new(
        session_id: impl Into<String>,
        user_id: impl Into<String>,
        plan: impl Into<String>,
        scheduled_utc: DateTime<Utc>,
        completed: bool,
    ) -> Self {
        Self {
            session_id: session_id.into(),
            user_id: user_id.into(),
            plan: plan.into(),
            scheduled_utc,
            completed,
        }
    }
}

/// (Sports) The sports-board contract.
///
/// Mirrors `interface ISportsBoard`.
pub trait ISportsBoard {
    /// Logs an activity.
    fn log(&self, a: Activity);
    /// The most-recent activities for a user, newest first (default `limit` 50).
    /// Panics when `limit <= 0` (mirrors the C# `ArgumentOutOfRangeException`).
    fn history(&self, user_id: &str, limit: i32) -> Vec<Activity>;
    /// Total kilometres of `kind` logged this calendar week (week starts Sunday,
    /// matching `System.DayOfWeek`).
    fn total_km_this_week(&self, user_id: &str, kind: DistanceKind, now: DateTime<Utc>) -> f64;
    /// The fastest activity of `kind` covering at least `distance_km`, as a
    /// [`PersonalBest`], if any.
    fn best(&self, user_id: &str, kind: DistanceKind, distance_km: f64) -> Option<PersonalBest>;
    /// Schedules (or overwrites) a training session.
    fn schedule(&self, s: TrainingSession);
    /// Marks a session complete. Panics on an unknown session id (mirrors the C#
    /// `InvalidOperationException`).
    fn complete(&self, session_id: &str);
    /// Upcoming, not-yet-completed sessions for a user, earliest first.
    fn upcoming(&self, user_id: &str) -> Vec<TrainingSession>;
}

/// (Sports) In-memory [`ISportsBoard`].
///
/// Mirrors `sealed class InMemorySportsBoard`.
pub struct InMemorySportsBoard {
    activities: Mutex<Vec<Activity>>,
    sessions: Mutex<HashMap<String, TrainingSession>>,
}

impl InMemorySportsBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            activities: Mutex::new(Vec::new()),
            sessions: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemorySportsBoard {
    fn default() -> Self {
        Self::new()
    }
}

/// Week start: `now.Date.AddDays(-(int)now.DayOfWeek)`. `System.DayOfWeek` is
/// Sunday = 0 … Saturday = 6; chrono's `weekday().num_days_from_sunday()` matches.
fn week_start(now: DateTime<Utc>) -> DateTime<Utc> {
    let dow = now.weekday().num_days_from_sunday() as i64;
    let date = now.date_naive() - Duration::days(dow);
    date.and_hms_opt(0, 0, 0).unwrap().and_utc()
}

impl ISportsBoard for InMemorySportsBoard {
    fn log(&self, a: Activity) {
        self.activities.lock().unwrap().push(a);
    }

    fn history(&self, user_id: &str, limit: i32) -> Vec<Activity> {
        if limit <= 0 {
            panic!("limit must be positive");
        }
        let acts = self.activities.lock().unwrap();
        let mut hits: Vec<Activity> = acts.iter().filter(|a| a.user_id == user_id).cloned().collect();
        // OrderByDescending(AtUtc) — stable.
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        hits.truncate(limit as usize);
        hits
    }

    fn total_km_this_week(&self, user_id: &str, kind: DistanceKind, now: DateTime<Utc>) -> f64 {
        let start = week_start(now);
        self.activities
            .lock()
            .unwrap()
            .iter()
            .filter(|a| a.user_id == user_id && a.kind == kind && a.at_utc >= start)
            .map(|a| a.distance_km)
            .sum()
    }

    fn best(&self, user_id: &str, kind: DistanceKind, distance_km: f64) -> Option<PersonalBest> {
        let acts = self.activities.lock().unwrap();
        // OrderBy(Duration).FirstOrDefault() — stable minimum by duration.
        let mut best: Option<&Activity> = None;
        for a in acts
            .iter()
            .filter(|a| a.user_id == user_id && a.kind == kind && a.distance_km >= distance_km)
        {
            match best {
                Some(b) if a.duration < b.duration => best = Some(a),
                None => best = Some(a),
                _ => {}
            }
        }
        best.map(|hit| PersonalBest::new(user_id, kind, distance_km, hit.duration, hit.at_utc))
    }

    fn schedule(&self, s: TrainingSession) {
        self.sessions.lock().unwrap().insert(s.session_id.clone(), s);
    }

    fn complete(&self, session_id: &str) {
        let mut sessions = self.sessions.lock().unwrap();
        match sessions.get(session_id) {
            Some(s) => {
                let updated = TrainingSession {
                    completed: true,
                    ..s.clone()
                };
                sessions.insert(session_id.to_string(), updated);
            }
            None => panic!("Unknown session {session_id}"),
        }
    }

    fn upcoming(&self, user_id: &str) -> Vec<TrainingSession> {
        let now = Utc::now();
        let mut hits: Vec<TrainingSession> = self
            .sessions
            .lock()
            .unwrap()
            .values()
            .filter(|s| s.user_id == user_id && !s.completed && s.scheduled_utc >= now)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.scheduled_utc.cmp(&b.scheduled_utc));
        hits
    }
}
