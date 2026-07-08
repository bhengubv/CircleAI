//! stores.rs
//!
//! Store traits for memory persistence: AffectState, PersonaState,
//! EpisodicMemoryEntry, FeedbackSignal, and Goal.

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::sync::Mutex;
use uuid::Uuid;

use super::affect_state::AffectState;
use super::goal::Goal;

// ─────────────────────────────────────────────────────────────────────────────
// PersonaState
// ─────────────────────────────────────────────────────────────────────────────

/// B!'s dynamic persona state for a specific user. Persisted between sessions
/// and injected into the system prompt to shape tone, vocabulary, and topical depth.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PersonaState {
    /// Opaque user identifier.
    pub user_id: String,

    /// UTC time of the last update.
    pub last_updated_at: DateTime<Utc>,

    /// Preferred response verbosity: `"brief"`, `"balanced"` (default), `"detailed"`.
    pub verbosity: String,

    /// Formality level: `"casual"`, `"neutral"` (default), `"formal"`.
    pub formality: String,

    /// Preferred response language/locale (IETF BCP-47).
    /// `None` means "match the device locale".
    pub preferred_locale: Option<String>,

    /// Weighted topic interests accumulated from positive interactions.
    pub topic_weights: HashMap<String, f32>,

    /// Topics the user has down-voted or explicitly rejected.
    pub disfavoured_topics: HashSet<String>,

    /// Total number of recorded interactions.
    pub total_interactions: i32,

    /// Cumulative positive feedback signals.
    pub positive_signals: i32,

    /// Cumulative negative feedback signals.
    pub negative_signals: i32,
}

impl Default for PersonaState {
    fn default() -> Self {
        Self {
            user_id: "default".to_string(),
            last_updated_at: Utc::now(),
            verbosity: "balanced".to_string(),
            formality: "neutral".to_string(),
            preferred_locale: None,
            topic_weights: HashMap::new(),
            disfavoured_topics: HashSet::new(),
            total_interactions: 0,
            positive_signals: 0,
            negative_signals: 0,
        }
    }
}

impl PersonaState {
    pub fn new(user_id: impl Into<String>) -> Self {
        Self {
            user_id: user_id.into(),
            ..Default::default()
        }
    }

    /// Derived satisfaction score 0.0–1.0.
    /// Returns `None` when insufficient data (fewer than 10 signals).
    pub fn satisfaction_score(&self) -> Option<f64> {
        let total = self.positive_signals + self.negative_signals;
        if total < 10 {
            None
        } else {
            Some(self.positive_signals as f64 / total as f64)
        }
    }

    /// Builds a compact persona instruction block suitable for prepending to the
    /// B! system prompt. Returns an empty string when the persona is default/unlearned.
    pub fn to_system_prompt_hint(&self) -> String {
        let mut hints: Vec<String> = Vec::new();

        if self.verbosity != "balanced" {
            hints.push(format!("Keep responses {}.", self.verbosity));
        }

        match self.formality.as_str() {
            "casual" => hints.push("Use a casual, friendly tone.".to_string()),
            "formal" => hints.push("Maintain a formal, professional tone.".to_string()),
            _ => {}
        }

        if let Some(ref locale) = self.preferred_locale {
            if !locale.trim().is_empty() {
                hints.push(format!(
                    "Respond in the language appropriate for locale {}.",
                    locale
                ));
            }
        }

        if hints.is_empty() {
            return String::new();
        }
        format!("[User preferences]\n{}\n", hints.join("\n"))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// EpisodicMemoryEntry
// ─────────────────────────────────────────────────────────────────────────────

/// A single recorded episode (one user↔assistant exchange).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EpisodicMemoryEntry {
    /// Stable identifier for the entry.
    pub id: Uuid,

    /// UTC timestamp of the assistant's response.
    pub recorded_at_utc: DateTime<Utc>,

    /// The user's message text.
    pub user_text: String,

    /// The assistant's response text.
    pub assistant_text: String,

    /// Optional identifier for the app context (e.g. `"tgn.bidbaas"`).
    pub app_context: Option<String>,

    /// L2-normalised embedding of `user_text + " " + assistant_text`,
    /// pre-computed at write time. `None` if the embedding backend was unavailable.
    pub embedding: Option<Vec<f32>>,

    /// Arbitrary key-value tags (e.g. `locale`, `sentiment`).
    pub tags: Option<HashMap<String, String>>,
}

impl Default for EpisodicMemoryEntry {
    fn default() -> Self {
        Self {
            id: Uuid::new_v4(),
            recorded_at_utc: Utc::now(),
            user_text: String::new(),
            assistant_text: String::new(),
            app_context: None,
            embedding: None,
            tags: None,
        }
    }
}

impl EpisodicMemoryEntry {
    pub fn new(user_text: impl Into<String>, assistant_text: impl Into<String>) -> Self {
        Self {
            user_text: user_text.into(),
            assistant_text: assistant_text.into(),
            ..Default::default()
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FeedbackSignal
// ─────────────────────────────────────────────────────────────────────────────

/// Polarity of the feedback signal.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[repr(i8)]
pub enum FeedbackPolarity {
    /// User explicitly approved / up-voted the response.
    Positive = 1,
    /// User explicitly rejected / down-voted the response.
    Negative = -1,
    /// User provided a correction (neutral polarity).
    Correction = 0,
}

/// A single user-feedback event tied to a specific B! response.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FeedbackSignal {
    /// Stable identifier for the signal.
    pub id: Uuid,

    /// UTC time when the user provided the signal.
    pub recorded_at_utc: DateTime<Utc>,

    /// The `EpisodicMemoryEntry::id` of the episode this feedback refers to.
    pub episode_id: Option<Uuid>,

    /// The user's original message.
    pub user_text: String,

    /// B!'s response that is being rated.
    pub assistant_text: String,

    /// User's rating.
    pub polarity: FeedbackPolarity,

    /// For `Correction` signals — the user's preferred response.
    pub corrected_text: Option<String>,

    /// Free-text comment the user optionally attached.
    pub comment: Option<String>,
}

impl FeedbackSignal {
    pub fn new(
        user_text: impl Into<String>,
        assistant_text: impl Into<String>,
        polarity: FeedbackPolarity,
    ) -> Self {
        Self {
            id: Uuid::new_v4(),
            recorded_at_utc: Utc::now(),
            episode_id: None,
            user_text: user_text.into(),
            assistant_text: assistant_text.into(),
            polarity,
            corrected_text: None,
            comment: None,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Store traits (async via async-trait)
// ─────────────────────────────────────────────────────────────────────────────

type BoxError = Box<dyn std::error::Error + Send + Sync>;

/// Async store for [`AffectState`] per user.
#[async_trait]
pub trait AffectStore: Send + Sync {
    /// Loads the affect state for `user_id`. Returns a fresh default state when none is found.
    async fn load(&self, user_id: &str) -> Result<AffectState, BoxError>;
    /// Persists the affect state.
    async fn save(&self, state: &AffectState) -> Result<(), BoxError>;
}

/// Async store for [`PersonaState`] per user.
#[async_trait]
pub trait PersonaStore: Send + Sync {
    async fn load(&self, user_id: &str) -> Result<PersonaState, BoxError>;
    async fn save(&self, persona: &PersonaState) -> Result<(), BoxError>;
}

/// Async persistent store for episodic memories.
#[async_trait]
pub trait EpisodicMemoryStore: Send + Sync {
    async fn save(&self, entry: &EpisodicMemoryEntry) -> Result<(), BoxError>;
    async fn get_recent(&self, user_id: &str, limit: usize) -> Result<Vec<EpisodicMemoryEntry>, BoxError>;
    async fn delete(&self, id: &str) -> Result<(), BoxError>;
}

/// Async store for user feedback signals.
#[async_trait]
pub trait FeedbackStore: Send + Sync {
    async fn add(&self, signal: FeedbackSignal) -> Result<(), BoxError>;
    async fn get_recent(&self, user_id: &str, count: usize) -> Result<Vec<FeedbackSignal>, BoxError>;
    async fn count(&self, user_id: &str) -> Result<usize, BoxError>;
}

/// Async store for [`Goal`] records.
#[async_trait]
pub trait GoalStore: Send + Sync {
    async fn list(&self, user_id: &str) -> Result<Vec<Goal>, BoxError>;
    async fn get(&self, id: &str) -> Result<Option<Goal>, BoxError>;
    async fn upsert(&self, goal: Goal) -> Result<Goal, BoxError>;
    async fn delete(&self, id: &str) -> Result<(), BoxError>;
    async fn get_active(&self, user_id: &str) -> Result<Vec<Goal>, BoxError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Sync store traits (kept for portability / no-std targets)
// ─────────────────────────────────────────────────────────────────────────────

/// Synchronous store for [`AffectState`].
pub trait IAffectStore {
    type Error: std::error::Error;

    fn load(&self, user_id: &str) -> Result<AffectState, Self::Error>;
    fn save(&mut self, state: &AffectState) -> Result<(), Self::Error>;
}

/// Synchronous store for [`PersonaState`].
pub trait IPersonaStore {
    type Error: std::error::Error;

    fn load(&self, user_id: &str) -> Result<PersonaState, Self::Error>;
    fn save(&mut self, persona: &PersonaState) -> Result<(), Self::Error>;
}

/// Synchronous persistent store for episodic memories.
pub trait IEpisodicMemoryStore {
    type Error: std::error::Error;

    fn add(&mut self, entry: EpisodicMemoryEntry) -> Result<(), Self::Error>;
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, Self::Error>;
    fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, Self::Error>;
    fn count(&self) -> Result<usize, Self::Error>;
    fn prune_older_than(&mut self, cutoff: &DateTime<Utc>) -> Result<usize, Self::Error>;
}

/// Synchronous store for feedback signals.
pub trait IFeedbackStore {
    type Error: std::error::Error;

    fn add(&mut self, signal: FeedbackSignal) -> Result<(), Self::Error>;
    fn get_recent(&self, count: usize) -> Result<Vec<FeedbackSignal>, Self::Error>;
    fn count(&self) -> Result<usize, Self::Error>;
    fn positive_ratio(&self) -> Result<Option<f64>, Self::Error>;
}

/// Synchronous store for [`Goal`] records.
pub trait IGoalStore {
    type Error: std::error::Error;

    fn list(&self, user_id: &str) -> Result<Vec<Goal>, Self::Error>;
    fn get(&self, id: &str) -> Result<Option<Goal>, Self::Error>;
    fn upsert(&mut self, goal: Goal) -> Result<Goal, Self::Error>;
    fn delete(&mut self, id: &str) -> Result<(), Self::Error>;
    fn get_active(&self, user_id: &str) -> Result<Vec<Goal>, Self::Error>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory sync stores (ports of the C# InMemoryPersonaStore / InMemoryGoalStore)
// ─────────────────────────────────────────────────────────────────────────────

use crate::brain::BrainError;
use crate::memory::goal::GoalStatus;

/// Thread-safe in-memory [`IGoalStore`]. 1:1 with the C# `InMemoryGoalStore`.
/// All data is lost when the process exits. Insertion order is preserved so
/// `list` / `get_active` are deterministic (the C# `ConcurrentDictionary.Values`
/// order is unspecified; keeping insertion order is a strict superset).
#[derive(Debug, Default)]
pub struct InMemoryGoalStore {
    // (insertion-ordered id list, id -> goal)
    inner: Mutex<GoalStoreInner>,
}

#[derive(Debug, Default)]
struct GoalStoreInner {
    order: Vec<String>,
    goals: HashMap<String, Goal>,
}

impl InMemoryGoalStore {
    pub fn new() -> Self {
        Self::default()
    }

    fn upsert_inner(&self, goal: Goal) -> Goal {
        let mut inner = self.inner.lock().unwrap();
        if !inner.goals.contains_key(&goal.id) {
            inner.order.push(goal.id.clone());
        }
        inner.goals.insert(goal.id.clone(), goal.clone());
        goal
    }

    fn delete_inner(&self, id: &str) {
        let mut inner = self.inner.lock().unwrap();
        if inner.goals.remove(id).is_some() {
            inner.order.retain(|x| x != id);
        }
    }

    fn list_inner(&self, user_id: &str) -> Vec<Goal> {
        let inner = self.inner.lock().unwrap();
        inner
            .order
            .iter()
            .filter_map(|id| inner.goals.get(id))
            .filter(|g| g.user_id == user_id)
            .cloned()
            .collect()
    }

    fn active_inner(&self, user_id: &str) -> Vec<Goal> {
        let inner = self.inner.lock().unwrap();
        inner
            .order
            .iter()
            .filter_map(|id| inner.goals.get(id))
            .filter(|g| g.user_id == user_id && g.status == GoalStatus::Active)
            .cloned()
            .collect()
    }
}

impl IGoalStore for InMemoryGoalStore {
    type Error = BrainError;

    fn list(&self, user_id: &str) -> Result<Vec<Goal>, BrainError> {
        if user_id.trim().is_empty() {
            return Err(BrainError::new("userId required"));
        }
        Ok(self.list_inner(user_id))
    }

    fn get(&self, id: &str) -> Result<Option<Goal>, BrainError> {
        if id.trim().is_empty() {
            return Err(BrainError::new("id required"));
        }
        Ok(self.inner.lock().unwrap().goals.get(id).cloned())
    }

    fn upsert(&mut self, goal: Goal) -> Result<Goal, BrainError> {
        Ok(self.upsert_inner(goal))
    }

    fn delete(&mut self, id: &str) -> Result<(), BrainError> {
        if id.trim().is_empty() {
            return Err(BrainError::new("id required"));
        }
        self.delete_inner(id);
        Ok(())
    }

    fn get_active(&self, user_id: &str) -> Result<Vec<Goal>, BrainError> {
        if user_id.trim().is_empty() {
            return Err(BrainError::new("userId required"));
        }
        Ok(self.active_inner(user_id))
    }
}
