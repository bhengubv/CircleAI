//! gaming — CircleAI gaming-board primitives.
//!
//! Full Rust port of `src/CircleAI.Gaming/GamingPrimitives.cs`:
//!
//! - Records [`GameTitle`] / [`PlaySession`] / [`AchievementUnlock`], the
//!   [`IGamingBoard`] contract, and the deterministic in-memory
//!   [`InMemoryGamingBoard`] (title catalogue + play sessions + total play time
//!   + achievements + most-played ranking).
//!
//! Sync-only; `TimeSpan Duration` → [`chrono::Duration`]; `DateTimeOffset` →
//! [`chrono::DateTime<Utc>`]. Play-time aggregation sums milliseconds, matching
//! the C# `Duration.TotalMilliseconds`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// Default `topK` for [`IGamingBoard::most_played`] (mirrors the C# `topK = 5`).
pub const DEFAULT_TOP_K: i32 = 5;

/// (Gaming) A game title.
///
/// Mirrors `sealed record GameTitle(string TitleId, string Name, string Genre,
/// string Platform)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GameTitle {
    pub title_id: String,
    pub name: String,
    pub genre: String,
    pub platform: String,
}

impl GameTitle {
    /// Constructs a title, mirroring the positional C# record constructor.
    pub fn new(
        title_id: impl Into<String>,
        name: impl Into<String>,
        genre: impl Into<String>,
        platform: impl Into<String>,
    ) -> Self {
        Self {
            title_id: title_id.into(),
            name: name.into(),
            genre: genre.into(),
            platform: platform.into(),
        }
    }
}

/// (Gaming) A play session.
///
/// Mirrors `sealed record PlaySession(string SessionId, string UserId,
/// string TitleId, TimeSpan Duration, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct PlaySession {
    pub session_id: String,
    pub user_id: String,
    pub title_id: String,
    pub duration: Duration,
    pub at_utc: DateTime<Utc>,
}

impl PlaySession {
    /// Constructs a session, mirroring the positional C# record constructor.
    pub fn new(
        session_id: impl Into<String>,
        user_id: impl Into<String>,
        title_id: impl Into<String>,
        duration: Duration,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            session_id: session_id.into(),
            user_id: user_id.into(),
            title_id: title_id.into(),
            duration,
            at_utc,
        }
    }
}

/// (Gaming) An achievement unlock.
///
/// Mirrors `sealed record AchievementUnlock(string UnlockId, string UserId,
/// string TitleId, string Achievement, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AchievementUnlock {
    pub unlock_id: String,
    pub user_id: String,
    pub title_id: String,
    pub achievement: String,
    pub at_utc: DateTime<Utc>,
}

impl AchievementUnlock {
    /// Constructs an unlock, mirroring the positional C# record constructor.
    pub fn new(
        unlock_id: impl Into<String>,
        user_id: impl Into<String>,
        title_id: impl Into<String>,
        achievement: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            unlock_id: unlock_id.into(),
            user_id: user_id.into(),
            title_id: title_id.into(),
            achievement: achievement.into(),
            at_utc,
        }
    }
}

/// (Gaming) The gaming-board contract.
///
/// Mirrors `interface IGamingBoard`.
pub trait IGamingBoard {
    /// Adds (or overwrites) a title.
    fn add_title(&self, t: GameTitle);
    /// A title by id, if any.
    fn get_title(&self, id: &str) -> Option<GameTitle>;
    /// Titles of a genre (case-insensitive).
    fn titles_by_genre(&self, genre: &str) -> Vec<GameTitle>;
    /// Records a play session.
    fn record_session(&self, s: PlaySession);
    /// Total play time for a user on a title.
    fn total_play_time(&self, user_id: &str, title_id: &str) -> Duration;
    /// Records an achievement unlock.
    fn unlock(&self, u: AchievementUnlock);
    /// A user's achievements, newest first.
    fn achievements_for(&self, user_id: &str) -> Vec<AchievementUnlock>;
    /// The user's `top_k` most-played titles by total play time (default
    /// [`DEFAULT_TOP_K`]). Panics when `top_k <= 0` (mirrors the C#
    /// `ArgumentOutOfRangeException`).
    fn most_played(&self, user_id: &str, top_k: i32) -> Vec<GameTitle>;
}

/// (Gaming) In-memory [`IGamingBoard`].
///
/// Mirrors `sealed class InMemoryGamingBoard`.
pub struct InMemoryGamingBoard {
    titles: Mutex<HashMap<String, GameTitle>>,
    sessions: Mutex<Vec<PlaySession>>,
    unlocks: Mutex<Vec<AchievementUnlock>>,
}

impl InMemoryGamingBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            titles: Mutex::new(HashMap::new()),
            sessions: Mutex::new(Vec::new()),
            unlocks: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryGamingBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IGamingBoard for InMemoryGamingBoard {
    fn add_title(&self, t: GameTitle) {
        self.titles.lock().unwrap().insert(t.title_id.clone(), t);
    }

    fn get_title(&self, id: &str) -> Option<GameTitle> {
        self.titles.lock().unwrap().get(id).cloned()
    }

    fn titles_by_genre(&self, genre: &str) -> Vec<GameTitle> {
        let target = genre.to_lowercase();
        self.titles
            .lock()
            .unwrap()
            .values()
            .filter(|t| t.genre.to_lowercase() == target)
            .cloned()
            .collect()
    }

    fn record_session(&self, s: PlaySession) {
        self.sessions.lock().unwrap().push(s);
    }

    fn total_play_time(&self, user_id: &str, title_id: &str) -> Duration {
        let ms: i64 = self
            .sessions
            .lock()
            .unwrap()
            .iter()
            .filter(|s| s.user_id == user_id && s.title_id == title_id)
            .map(|s| s.duration.num_milliseconds())
            .sum();
        Duration::milliseconds(ms)
    }

    fn unlock(&self, u: AchievementUnlock) {
        self.unlocks.lock().unwrap().push(u);
    }

    fn achievements_for(&self, user_id: &str) -> Vec<AchievementUnlock> {
        let mut hits: Vec<AchievementUnlock> = self
            .unlocks
            .lock()
            .unwrap()
            .iter()
            .filter(|u| u.user_id == user_id)
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        hits
    }

    fn most_played(&self, user_id: &str, top_k: i32) -> Vec<GameTitle> {
        if top_k <= 0 {
            panic!("top_k must be positive");
        }
        let sessions = self.sessions.lock().unwrap();
        // Group by title, summing milliseconds. Preserve first-seen order for
        // stable ordering among equal totals (mirrors LINQ GroupBy/OrderBy stability).
        let mut order: Vec<String> = Vec::new();
        let mut totals: HashMap<String, i64> = HashMap::new();
        for s in sessions.iter().filter(|s| s.user_id == user_id) {
            if !totals.contains_key(&s.title_id) {
                order.push(s.title_id.clone());
            }
            *totals.entry(s.title_id.clone()).or_insert(0) += s.duration.num_milliseconds();
        }
        drop(sessions);
        // OrderByDescending(total) — stable, so keep first-seen order on ties.
        order.sort_by(|a, b| totals[b].cmp(&totals[a]));
        let titles = self.titles.lock().unwrap();
        order
            .into_iter()
            .take(top_k as usize)
            .filter_map(|tid| titles.get(&tid).cloned())
            .collect()
    }
}
