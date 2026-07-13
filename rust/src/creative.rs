//! creative — CircleAI creative-board primitives.
//!
//! Full Rust port of `src/CircleAI.Creative/CreativePrimitives.cs`:
//!
//! - Records [`CreativeWork`] / [`Inspiration`] / [`Critique`], the
//!   [`ICreativeBoard`] contract, and the deterministic in-memory
//!   [`InMemoryCreativeBoard`] (works by tag + inspiration log + critiques +
//!   average score).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// Default `limit` for [`ICreativeBoard::recent_inspiration`] (C# `limit = 20`).
pub const DEFAULT_INSPIRATION_LIMIT: i32 = 20;

/// (Creative) A creative work.
///
/// Mirrors `sealed record CreativeWork(string WorkId, string Title,
/// string Medium, string Author, DateTimeOffset CreatedUtc,
/// IReadOnlyList<string> Tags)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreativeWork {
    pub work_id: String,
    pub title: String,
    pub medium: String,
    pub author: String,
    pub created_utc: DateTime<Utc>,
    pub tags: Vec<String>,
}

impl CreativeWork {
    /// Constructs a work, mirroring the positional C# record constructor.
    pub fn new(
        work_id: impl Into<String>,
        title: impl Into<String>,
        medium: impl Into<String>,
        author: impl Into<String>,
        created_utc: DateTime<Utc>,
        tags: Vec<String>,
    ) -> Self {
        Self {
            work_id: work_id.into(),
            title: title.into(),
            medium: medium.into(),
            author: author.into(),
            created_utc,
            tags,
        }
    }
}

/// (Creative) A captured inspiration.
///
/// Mirrors `sealed record Inspiration(string InspirationId, string PromptText,
/// string SourceUrl, DateTimeOffset SeenUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Inspiration {
    pub inspiration_id: String,
    pub prompt_text: String,
    pub source_url: String,
    pub seen_utc: DateTime<Utc>,
}

impl Inspiration {
    /// Constructs an inspiration, mirroring the positional C# record constructor.
    pub fn new(
        inspiration_id: impl Into<String>,
        prompt_text: impl Into<String>,
        source_url: impl Into<String>,
        seen_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            inspiration_id: inspiration_id.into(),
            prompt_text: prompt_text.into(),
            source_url: source_url.into(),
            seen_utc,
        }
    }
}

/// (Creative) A critique of a work.
///
/// Mirrors `sealed record Critique(string CritiqueId, string WorkId,
/// string Reviewer, string Body, int Score)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Critique {
    pub critique_id: String,
    pub work_id: String,
    pub reviewer: String,
    pub body: String,
    pub score: i32,
}

impl Critique {
    /// Constructs a critique, mirroring the positional C# record constructor.
    pub fn new(
        critique_id: impl Into<String>,
        work_id: impl Into<String>,
        reviewer: impl Into<String>,
        body: impl Into<String>,
        score: i32,
    ) -> Self {
        Self {
            critique_id: critique_id.into(),
            work_id: work_id.into(),
            reviewer: reviewer.into(),
            body: body.into(),
            score,
        }
    }
}

/// (Creative) The creative-board contract.
///
/// Mirrors `interface ICreativeBoard`.
pub trait ICreativeBoard {
    /// Adds (or overwrites) a work.
    fn add_work(&self, w: CreativeWork);
    /// A work by id, if any.
    fn get_work(&self, id: &str) -> Option<CreativeWork>;
    /// Works carrying a tag (case-insensitive).
    fn works_by_tag(&self, tag: &str) -> Vec<CreativeWork>;
    /// Records an inspiration.
    fn record_inspiration(&self, i: Inspiration);
    /// The most-recent inspirations, newest first (default
    /// [`DEFAULT_INSPIRATION_LIMIT`]).
    fn recent_inspiration(&self, limit: i32) -> Vec<Inspiration>;
    /// Adds a critique.
    fn add_critique(&self, c: Critique);
    /// The average critique score for a work; `0.0` when there are none.
    fn avg_score(&self, work_id: &str) -> f64;
}

/// (Creative) In-memory [`ICreativeBoard`].
///
/// Mirrors `sealed class InMemoryCreativeBoard`.
pub struct InMemoryCreativeBoard {
    works: Mutex<HashMap<String, CreativeWork>>,
    inspiration: Mutex<Vec<Inspiration>>,
    critiques: Mutex<Vec<Critique>>,
}

impl InMemoryCreativeBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            works: Mutex::new(HashMap::new()),
            inspiration: Mutex::new(Vec::new()),
            critiques: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryCreativeBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ICreativeBoard for InMemoryCreativeBoard {
    fn add_work(&self, w: CreativeWork) {
        self.works.lock().unwrap().insert(w.work_id.clone(), w);
    }

    fn get_work(&self, id: &str) -> Option<CreativeWork> {
        self.works.lock().unwrap().get(id).cloned()
    }

    fn works_by_tag(&self, tag: &str) -> Vec<CreativeWork> {
        let target = tag.to_lowercase();
        self.works
            .lock()
            .unwrap()
            .values()
            .filter(|w| w.tags.iter().any(|t| t.to_lowercase() == target))
            .cloned()
            .collect()
    }

    fn record_inspiration(&self, i: Inspiration) {
        self.inspiration.lock().unwrap().push(i);
    }

    fn recent_inspiration(&self, limit: i32) -> Vec<Inspiration> {
        let mut hits: Vec<Inspiration> = self.inspiration.lock().unwrap().clone();
        hits.sort_by(|a, b| b.seen_utc.cmp(&a.seen_utc));
        if limit >= 0 {
            hits.truncate(limit as usize);
        }
        hits
    }

    fn add_critique(&self, c: Critique) {
        self.critiques.lock().unwrap().push(c);
    }

    fn avg_score(&self, work_id: &str) -> f64 {
        let critiques = self.critiques.lock().unwrap();
        let scores: Vec<f64> = critiques
            .iter()
            .filter(|c| c.work_id == work_id)
            .map(|c| c.score as f64)
            .collect();
        if scores.is_empty() {
            // DefaultIfEmpty(0).Average() → 0.
            0.0
        } else {
            scores.iter().sum::<f64>() / scores.len() as f64
        }
    }
}

/// StubGuard parity additions — concrete-only helpers on the in-memory board
/// (mirroring the C# members added to `InMemoryCreativeBoard`/`ICreativeBoard`).
impl InMemoryCreativeBoard {
    /// Number of catalogued works. Mirrors `WorkCount`.
    pub fn work_count(&self) -> usize {
        self.works.lock().unwrap().len()
    }

    /// Removes a work and cascades: drops all its critiques. Returns `true` if the
    /// work was present. Mirrors `RemoveWork`.
    pub fn remove_work(&self, work_id: &str) -> bool {
        let removed = self.works.lock().unwrap().remove(work_id).is_some();
        if removed {
            self.critiques.lock().unwrap().retain(|c| c.work_id != work_id);
        }
        removed
    }

    /// Works by `author` (case-insensitive), newest first. Mirrors `WorksByAuthor`.
    pub fn works_by_author(&self, author: &str) -> Vec<CreativeWork> {
        let mut hits: Vec<CreativeWork> = self
            .works
            .lock()
            .unwrap()
            .values()
            .filter(|w| w.author.eq_ignore_ascii_case(author))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.created_utc.cmp(&a.created_utc));
        hits
    }

    /// Works in `medium` (case-insensitive), newest first. Mirrors `WorksByMedium`.
    pub fn works_by_medium(&self, medium: &str) -> Vec<CreativeWork> {
        let mut hits: Vec<CreativeWork> = self
            .works
            .lock()
            .unwrap()
            .values()
            .filter(|w| w.medium.eq_ignore_ascii_case(medium))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.created_utc.cmp(&a.created_utc));
        hits
    }

    /// The work with the highest average critique score, if any (the highest-avg
    /// work id that still resolves to a work). Mirrors `TopRatedWork`.
    pub fn top_rated_work(&self) -> Option<CreativeWork> {
        let critiques = self.critiques.lock().unwrap();
        // Average score per work id.
        let mut sums: HashMap<String, (f64, usize)> = HashMap::new();
        for c in critiques.iter() {
            let e = sums.entry(c.work_id.clone()).or_insert((0.0, 0));
            e.0 += c.score as f64;
            e.1 += 1;
        }
        let mut ranked: Vec<(String, f64)> = sums
            .into_iter()
            .map(|(id, (sum, n))| (id, sum / n as f64))
            .collect();
        // Highest average first.
        ranked.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        let works = self.works.lock().unwrap();
        ranked
            .into_iter()
            .find_map(|(id, _)| works.get(&id).cloned())
    }

    /// All distinct tags across every work (case-insensitive distinct, keeping the
    /// first-seen casing), sorted case-insensitively ascending. Mirrors `AllTags`.
    pub fn all_tags(&self) -> Vec<String> {
        let works = self.works.lock().unwrap();
        let mut seen: HashMap<String, ()> = HashMap::new();
        let mut out: Vec<String> = Vec::new();
        for w in works.values() {
            for t in &w.tags {
                let key = t.to_lowercase();
                if seen.insert(key, ()).is_none() {
                    out.push(t.clone());
                }
            }
        }
        out.sort_by(|a, b| a.to_lowercase().cmp(&b.to_lowercase()));
        out
    }
}
