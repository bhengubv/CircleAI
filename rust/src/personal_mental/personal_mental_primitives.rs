//! personal_mental_primitives.rs
//!
//! (3.3.0) Real domain types + in-memory store for the mental-health vertical —
//! Rust port of `src/CircleAI.Personal.Mental/PersonalMentalPrimitives.cs`: mood
//! logs, journal entries, coping-strategy library, 7-day trend.
//!
//! Privacy: per-user instance only. Moods live in a `Mutex<Vec>` (C# `List` +
//! `object _lock`); entries and strategies in `Mutex<HashMap>` (C#
//! `ConcurrentDictionary`). The `Last7Days` cutoff is `Utc::now() - 7 days`,
//! matching `DateTimeOffset.UtcNow.AddDays(-7)`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// (3.3.0) A mood level.
///
/// Mirrors `enum Mood { VeryLow, Low, Neutral, Good, Great }`; discriminants
/// match the C# declaration order (`VeryLow = 0 … Great = 4`), so
/// `AvgMood7Day`'s `(int)m.Mood` average is reproduced by casting to the same
/// integer values.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum Mood {
    VeryLow = 0,
    Low = 1,
    Neutral = 2,
    Good = 3,
    Great = 4,
}

/// (3.3.0) A logged mood.
///
/// Mirrors `sealed record MoodLog(Mood Mood, DateTimeOffset AtUtc,
/// string? Note)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MoodLog {
    pub mood: Mood,
    pub at_utc: DateTime<Utc>,
    pub note: Option<String>,
}

impl MoodLog {
    /// Constructs a mood log, mirroring the positional C# record constructor.
    pub fn new(mood: Mood, at_utc: DateTime<Utc>, note: Option<String>) -> Self {
        Self { mood, at_utc, note }
    }
}

/// (3.3.0) A journal entry.
///
/// Mirrors `sealed record JournalEntry(string EntryId, string Title,
/// string Body, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JournalEntry {
    pub entry_id: String,
    pub title: String,
    pub body: String,
    pub at_utc: DateTime<Utc>,
}

impl JournalEntry {
    /// Constructs an entry, mirroring the positional C# record constructor.
    pub fn new(
        entry_id: impl Into<String>,
        title: impl Into<String>,
        body: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            entry_id: entry_id.into(),
            title: title.into(),
            body: body.into(),
            at_utc,
        }
    }
}

/// (3.3.0) A coping strategy.
///
/// Mirrors `sealed record CopingStrategy(string StrategyId, string Title,
/// string Description, IReadOnlyList<string> Tags)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CopingStrategy {
    pub strategy_id: String,
    pub title: String,
    pub description: String,
    pub tags: Vec<String>,
}

impl CopingStrategy {
    /// Constructs a strategy, mirroring the positional C# record constructor.
    pub fn new(
        strategy_id: impl Into<String>,
        title: impl Into<String>,
        description: impl Into<String>,
        tags: Vec<String>,
    ) -> Self {
        Self {
            strategy_id: strategy_id.into(),
            title: title.into(),
            description: description.into(),
            tags,
        }
    }
}

/// (3.3.0) The Mental Health board contract.
///
/// Mirrors `interface IMentalHealthBoard`. The `Entries` getter becomes
/// [`entries`](IMentalHealthBoard::entries).
pub trait IMentalHealthBoard {
    /// Logs a mood.
    fn log_mood(&self, m: MoodLog);
    /// Mood logs from the last 7 days, oldest-first.
    fn last_7_days(&self) -> Vec<MoodLog>;
    /// Adds (or overwrites) a journal entry. Panics on a blank id (C#
    /// `ArgumentException`).
    fn add_entry(&self, e: JournalEntry);
    /// All journal entries, newest-first.
    fn entries(&self) -> Vec<JournalEntry>;
    /// Registers (or overwrites) a coping strategy.
    fn register_strategy(&self, s: CopingStrategy);
    /// Strategies tagged `tag` (case-insensitive). Panics on a blank tag (C#
    /// `ArgumentException`).
    fn strategies_by_tag(&self, tag: &str) -> Vec<CopingStrategy>;
    /// Mean mood over the last 7 days as `(int)Mood` values; `NaN` when empty.
    fn avg_mood_7_day(&self) -> f64;
}

/// (3.3.0) In-memory [`IMentalHealthBoard`].
pub struct InMemoryMentalHealthBoard {
    moods: Mutex<Vec<MoodLog>>,
    entries: Mutex<HashMap<String, JournalEntry>>,
    strats: Mutex<HashMap<String, CopingStrategy>>,
}

impl InMemoryMentalHealthBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            moods: Mutex::new(Vec::new()),
            entries: Mutex::new(HashMap::new()),
            strats: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryMentalHealthBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IMentalHealthBoard for InMemoryMentalHealthBoard {
    fn log_mood(&self, m: MoodLog) {
        self.moods.lock().unwrap().push(m);
    }

    fn last_7_days(&self) -> Vec<MoodLog> {
        let cutoff = Utc::now() - Duration::days(7);
        let mut out: Vec<MoodLog> = self
            .moods
            .lock()
            .unwrap()
            .iter()
            .filter(|m| m.at_utc >= cutoff)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        out
    }

    fn add_entry(&self, e: JournalEntry) {
        if e.entry_id.trim().is_empty() {
            panic!("EntryId required");
        }
        self.entries.lock().unwrap().insert(e.entry_id.clone(), e);
    }

    fn entries(&self) -> Vec<JournalEntry> {
        let mut out: Vec<JournalEntry> = self.entries.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out
    }

    fn register_strategy(&self, s: CopingStrategy) {
        self.strats.lock().unwrap().insert(s.strategy_id.clone(), s);
    }

    fn strategies_by_tag(&self, tag: &str) -> Vec<CopingStrategy> {
        if tag.trim().is_empty() {
            panic!("tag required");
        }
        self.strats
            .lock()
            .unwrap()
            .values()
            .filter(|s| s.tags.iter().any(|t| t.eq_ignore_ascii_case(tag)))
            .cloned()
            .collect()
    }

    fn avg_mood_7_day(&self) -> f64 {
        let items = self.last_7_days();
        if items.is_empty() {
            return f64::NAN;
        }
        let sum: i64 = items.iter().map(|m| m.mood as i64).sum();
        sum as f64 / items.len() as f64
    }
}
