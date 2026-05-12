//! memory.rs
//!
//! AffectState, PersonaState, EpisodicMemoryEntry, FeedbackSignal, Goal, and
//! their store traits — the "HER affect + memory layer".

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use uuid::Uuid;

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

fn lerp(a: f32, b: f32, t: f32) -> f32 {
    let tc = t.clamp(0.0, 1.0);
    a + (b - a) * tc
}

// ─────────────────────────────────────────────────────────────────────────────
// AffectState
// ─────────────────────────────────────────────────────────────────────────────

/// B!'s current emotional/engagement state — the "HER affect layer".
///
/// Five float dimensions, all 0.0–1.0. Persisted per-user and injected
/// into the system prompt to shape response tone and initiative.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AffectState {
    /// Opaque user identifier (device ID or hashed phone number).
    /// Never contains PII in plaintext.
    pub user_id: String,

    /// UTC time of the last update to this affect state.
    pub last_updated_at: DateTime<Utc>,

    /// 0 = bored, 1 = fascinated. Drives proactive questions.
    pub curiosity: f32,

    /// 0 = disengaged, 1 = fully engaged. Rises with frequent quality interactions.
    pub engagement: f32,

    /// 0 = confident, 1 = confused. High → ask clarifying questions.
    pub uncertainty: f32,

    /// 0 = stranger, 1 = deep rapport. Grows slowly over many sessions.
    pub rapport: f32,

    /// 0 = subdued, 1 = energetic. Mirrors time-of-day and interaction pace.
    pub energy: f32,
}

impl Default for AffectState {
    fn default() -> Self {
        Self {
            user_id: "default".to_string(),
            last_updated_at: Utc::now(),
            curiosity: 0.5,
            engagement: 0.5,
            uncertainty: 0.2,
            rapport: 0.0,
            energy: 0.5,
        }
    }
}

impl AffectState {
    /// Create a new default `AffectState` for the given user.
    pub fn new(user_id: impl Into<String>) -> Self {
        Self {
            user_id: user_id.into(),
            ..Default::default()
        }
    }

    /// Apply a positive interaction: nudge `engagement` and `rapport` up, `uncertainty` down.
    pub fn apply_positive_signal(&mut self) {
        self.engagement = (self.engagement + 0.02).clamp(0.0, 1.0);
        self.rapport = (self.rapport + 0.01).clamp(0.0, 1.0);
        self.uncertainty = (self.uncertainty - 0.02).clamp(0.0, 1.0);
        self.last_updated_at = Utc::now();
    }

    /// Apply a negative interaction: nudge `engagement` down, `uncertainty` up.
    pub fn apply_negative_signal(&mut self) {
        self.engagement = (self.engagement - 0.03).clamp(0.0, 1.0);
        self.uncertainty = (self.uncertainty + 0.03).clamp(0.0, 1.0);
        self.last_updated_at = Utc::now();
    }

    /// Apply idle time decay: `engagement` and `energy` drift back toward 0.5.
    pub fn apply_idle_decay(&mut self, idle_hours: f32) {
        let decay = (idle_hours * 0.02_f32).min(0.3_f32);
        self.engagement = lerp(self.engagement, 0.5, decay);
        self.energy = lerp(self.energy, 0.5, decay);
        self.last_updated_at = Utc::now();
    }

    /// Builds a compact affect hint for injection into the system prompt.
    ///
    /// Only emits lines that deviate meaningfully from neutral (0.5).
    pub fn to_system_prompt_hint(&self) -> String {
        let mut hints: Vec<&str> = Vec::new();

        if self.curiosity > 0.7 {
            hints.push("You are deeply curious about this topic — ask a follow-up question.");
        }
        if self.engagement > 0.7 {
            hints.push("You are fully engaged — be enthusiastic and thorough.");
        }
        if self.engagement < 0.3 {
            hints.push("Keep your response brief and to the point.");
        }
        if self.uncertainty > 0.6 {
            hints.push("You are uncertain — ask a clarifying question before answering.");
        }
        if self.rapport > 0.7 {
            hints.push("You know this user well — use a warm, familiar tone.");
        }
        if self.energy < 0.3 {
            hints.push("Keep your response calm and measured.");
        }
        if self.energy > 0.8 {
            hints.push("You are energetic — be upbeat and concise.");
        }

        if hints.is_empty() {
            return String::new();
        }
        format!("[Affect state]\n{}\n", hints.join("\n"))
    }
}

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
// Goal
// ─────────────────────────────────────────────────────────────────────────────

/// Lifecycle state of a [`Goal`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum GoalStatus {
    /// Goal is currently being pursued.
    Active,
    /// Goal has been achieved.
    Completed,
    /// Goal has been abandoned without completion.
    Abandoned,
}

/// Relative importance of a [`Goal`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum GoalPriority {
    /// Nice-to-have; may be deferred.
    Low,
    /// Standard importance.
    Normal,
    /// Urgent or critical to the user.
    High,
}

/// A user goal that B! tracks and proactively helps with.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Goal {
    /// Unique stable identifier for this goal.
    pub id: String,
    /// Owner of this goal.
    pub user_id: String,
    /// Short, human-readable title.
    pub title: String,
    /// Full description of what the user wants to achieve.
    pub description: String,
    /// Current lifecycle state.
    pub status: GoalStatus,
    /// Relative importance.
    pub priority: GoalPriority,
    /// When this goal was first recorded (UTC).
    pub created_utc: DateTime<Utc>,
    /// Optional deadline (UTC).
    pub due_utc: Option<DateTime<Utc>>,
    /// When the goal was completed or abandoned (UTC).
    pub completed_utc: Option<DateTime<Utc>>,
    /// Freeform notes B! or the user has attached to this goal.
    pub notes: Option<String>,
}

impl Goal {
    pub fn new(
        id: impl Into<String>,
        user_id: impl Into<String>,
        title: impl Into<String>,
        description: impl Into<String>,
        priority: GoalPriority,
    ) -> Self {
        Self {
            id: id.into(),
            user_id: user_id.into(),
            title: title.into(),
            description: description.into(),
            status: GoalStatus::Active,
            priority,
            created_utc: Utc::now(),
            due_utc: None,
            completed_utc: None,
            notes: None,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Store traits
// ─────────────────────────────────────────────────────────────────────────────

/// Loads and persists [`AffectState`] for a specific user.
///
/// Sync for portability — platform implementations add async execution context.
pub trait IAffectStore {
    type Error: std::error::Error;

    /// Loads the affect state for `user_id`. Returns a fresh default state when none is found.
    fn load(&self, user_id: &str) -> Result<AffectState, Self::Error>;

    /// Persists the affect state. Must be crash-safe (write-then-swap or similar).
    fn save(&mut self, state: &AffectState) -> Result<(), Self::Error>;
}

/// Loads and persists [`PersonaState`] for a specific user.
pub trait IPersonaStore {
    type Error: std::error::Error;

    /// Loads the persona for `user_id`. Returns a fresh default persona when none is found.
    fn load(&self, user_id: &str) -> Result<PersonaState, Self::Error>;

    /// Persists the persona. Must be crash-safe.
    fn save(&mut self, persona: &PersonaState) -> Result<(), Self::Error>;
}

/// Persistent store for episodic memories (conversational exchanges + embeddings).
pub trait IEpisodicMemoryStore {
    type Error: std::error::Error;

    /// Appends a new entry to the store.
    fn add(&mut self, entry: EpisodicMemoryEntry) -> Result<(), Self::Error>;

    /// Returns the `top_k` entries most similar (cosine) to `query_embedding`.
    /// When `query_embedding` is `None`, falls back to recency.
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, Self::Error>;

    /// Returns the most recent `count` entries, newest-first.
    fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, Self::Error>;

    /// Total number of entries currently stored.
    fn count(&self) -> Result<usize, Self::Error>;

    /// Removes all entries older than `cutoff`. Returns the number removed.
    fn prune_older_than(&mut self, cutoff: &DateTime<Utc>) -> Result<usize, Self::Error>;
}

/// Persists user feedback signals for later analysis and on-device adaptation.
pub trait IFeedbackStore {
    type Error: std::error::Error;

    /// Records a new feedback signal.
    fn add(&mut self, signal: FeedbackSignal) -> Result<(), Self::Error>;

    /// Returns the most recent `count` signals, newest-first.
    fn get_recent(&self, count: usize) -> Result<Vec<FeedbackSignal>, Self::Error>;

    /// Total number of signals stored.
    fn count(&self) -> Result<usize, Self::Error>;

    /// Fraction of stored signals that are [`FeedbackPolarity::Positive`] (0.0–1.0).
    /// Returns `None` when no signals are available.
    fn positive_ratio(&self) -> Result<Option<f64>, Self::Error>;
}

/// Persists and retrieves [`Goal`] records for a user.
pub trait IGoalStore {
    type Error: std::error::Error;

    /// Returns all goals for the given user, in any order.
    fn list(&self, user_id: &str) -> Result<Vec<Goal>, Self::Error>;

    /// Returns the goal with the given `id`, or `None` if it does not exist.
    fn get(&self, id: &str) -> Result<Option<Goal>, Self::Error>;

    /// Inserts or replaces the goal. The goal's `id` is the natural key.
    fn upsert(&mut self, goal: Goal) -> Result<Goal, Self::Error>;

    /// Deletes the goal with the given `id`. No-op if not found.
    fn delete(&mut self, id: &str) -> Result<(), Self::Error>;

    /// Returns all goals for `user_id` where `status == GoalStatus::Active`.
    fn get_active(&self, user_id: &str) -> Result<Vec<Goal>, Self::Error>;
}
