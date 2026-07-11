//! kids — CircleAI kids-board primitives.
//!
//! Full Rust port of `src/CircleAI.Kids/KidsPrimitives.cs`:
//!
//! - [`AgeAppropriateness`] enum, records [`KidsContent`] / [`DailyTime`] /
//!   [`TimeLog`], the [`IKidsBoard`] contract, and the deterministic in-memory
//!   [`InMemoryKidsBoard`] (age-banded content + per-child time limits + usage
//!   tracking + over-limit detection).
//!
//! Sync-only; `TimeSpan` → [`chrono::Duration`]; `DateTimeOffset` →
//! [`chrono::DateTime<Utc>`]. "Used today" compares calendar dates (`AtUtc.Date`).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// (Kids) Age-appropriateness band.
///
/// Mirrors `enum AgeAppropriateness { Toddler, Preschool, EarlyPrimary,
/// LatePrimary, PreTeen, Teen }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AgeAppropriateness {
    Toddler,
    Preschool,
    EarlyPrimary,
    LatePrimary,
    PreTeen,
    Teen,
}

/// (Kids) A piece of kids' content.
///
/// Mirrors `sealed record KidsContent(string ContentId, string Title,
/// AgeAppropriateness AgeBand, string Kind, IReadOnlyList<string> Tags)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KidsContent {
    pub content_id: String,
    pub title: String,
    pub age_band: AgeAppropriateness,
    pub kind: String,
    pub tags: Vec<String>,
}

impl KidsContent {
    /// Constructs content, mirroring the positional C# record constructor.
    pub fn new(
        content_id: impl Into<String>,
        title: impl Into<String>,
        age_band: AgeAppropriateness,
        kind: impl Into<String>,
        tags: Vec<String>,
    ) -> Self {
        Self {
            content_id: content_id.into(),
            title: title.into(),
            age_band,
            kind: kind.into(),
            tags,
        }
    }
}

/// (Kids) A child's daily time limits.
///
/// Mirrors `sealed record DailyTime(string KidName, TimeSpan ScreenLimit,
/// TimeSpan ReadingLimit)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DailyTime {
    pub kid_name: String,
    pub screen_limit: Duration,
    pub reading_limit: Duration,
}

impl DailyTime {
    /// Constructs limits, mirroring the positional C# record constructor.
    pub fn new(kid_name: impl Into<String>, screen_limit: Duration, reading_limit: Duration) -> Self {
        Self {
            kid_name: kid_name.into(),
            screen_limit,
            reading_limit,
        }
    }
}

/// (Kids) A logged activity duration.
///
/// Mirrors `sealed record TimeLog(string KidName, string Kind, TimeSpan Duration,
/// DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TimeLog {
    pub kid_name: String,
    pub kind: String,
    pub duration: Duration,
    pub at_utc: DateTime<Utc>,
}

impl TimeLog {
    /// Constructs a log, mirroring the positional C# record constructor.
    pub fn new(kid_name: impl Into<String>, kind: impl Into<String>, duration: Duration, at_utc: DateTime<Utc>) -> Self {
        Self {
            kid_name: kid_name.into(),
            kind: kind.into(),
            duration,
            at_utc,
        }
    }
}

/// (Kids) The kids-board contract.
///
/// Mirrors `interface IKidsBoard`.
pub trait IKidsBoard {
    /// Adds (or overwrites) content.
    fn add_content(&self, c: KidsContent);
    /// Content in an age band, by title.
    fn content_for(&self, band: AgeAppropriateness) -> Vec<KidsContent>;
    /// Sets (or overwrites) a child's limits.
    fn set_limits(&self, d: DailyTime);
    /// A child's limits, if any.
    fn limits_for(&self, kid_name: &str) -> Option<DailyTime>;
    /// Records a time log.
    fn record_time(&self, t: TimeLog);
    /// Total time of `kind` used by a child on `now`'s calendar date.
    fn used_today(&self, kid_name: &str, kind: &str, now: DateTime<Utc>) -> Duration;
    /// `true` when today's usage of `kind` exceeds the child's cap (screen /
    /// reading; other kinds are uncapped). `false` when the child has no limits.
    fn over_limit(&self, kid_name: &str, kind: &str, now: DateTime<Utc>) -> bool;
}

/// (Kids) In-memory [`IKidsBoard`].
///
/// Mirrors `sealed class InMemoryKidsBoard`.
pub struct InMemoryKidsBoard {
    content: Mutex<HashMap<String, KidsContent>>,
    limits: Mutex<HashMap<String, DailyTime>>,
    logs: Mutex<Vec<TimeLog>>,
}

impl InMemoryKidsBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            content: Mutex::new(HashMap::new()),
            limits: Mutex::new(HashMap::new()),
            logs: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryKidsBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IKidsBoard for InMemoryKidsBoard {
    fn add_content(&self, c: KidsContent) {
        self.content.lock().unwrap().insert(c.content_id.clone(), c);
    }

    fn content_for(&self, band: AgeAppropriateness) -> Vec<KidsContent> {
        let mut hits: Vec<KidsContent> = self
            .content
            .lock()
            .unwrap()
            .values()
            .filter(|c| c.age_band == band)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.title.cmp(&b.title));
        hits
    }

    fn set_limits(&self, d: DailyTime) {
        self.limits.lock().unwrap().insert(d.kid_name.clone(), d);
    }

    fn limits_for(&self, kid_name: &str) -> Option<DailyTime> {
        self.limits.lock().unwrap().get(kid_name).cloned()
    }

    fn record_time(&self, t: TimeLog) {
        self.logs.lock().unwrap().push(t);
    }

    fn used_today(&self, kid_name: &str, kind: &str, now: DateTime<Utc>) -> Duration {
        let today = now.date_naive();
        let ms: i64 = self
            .logs
            .lock()
            .unwrap()
            .iter()
            .filter(|l| l.kid_name == kid_name && l.kind == kind && l.at_utc.date_naive() == today)
            .map(|l| l.duration.num_milliseconds())
            .sum();
        Duration::milliseconds(ms)
    }

    fn over_limit(&self, kid_name: &str, kind: &str, now: DateTime<Utc>) -> bool {
        let limits = match self.limits_for(kid_name) {
            Some(l) => l,
            None => return false,
        };
        let used = self.used_today(kid_name, kind, now);
        let cap = if kind.eq_ignore_ascii_case("screen") {
            limits.screen_limit
        } else if kind.eq_ignore_ascii_case("reading") {
            limits.reading_limit
        } else {
            // TimeSpan.MaxValue — nothing exceeds it.
            Duration::MAX
        };
        used > cap
    }
}
