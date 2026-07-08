//! consolidation.rs
//!
//! Hierarchical memory consolidation — the "sleep cycle" engine. Ported from
//! CircleAI.Memory.Consolidation (C#): SleepKind, CoreMemory, DailyMemorySummary,
//! SemanticMemoryCluster, PersonaDeltaSnapshot, the four tier stores, the
//! HeuristicSummarizer, and the MemoryConsolidator orchestration engine — and
//! mirrors the TypeScript pilot (memory/consolidation.ts) 1:1.
//!
//! Promotes episodic → daily → weekly (semantic) → monthly (persona delta) →
//! core, and enforces retention. All time decisions go through an injectable
//! clock so tests are deterministic. This is the in-memory port: identical
//! algorithms and formulas to the C# reference, no persistence.
//!
//! C# `DateOnly` is represented here as a "YYYY-MM-DD" UTC `String`. ISO date
//! strings compare correctly with `Ord`, so the range/idempotency/prune
//! comparisons carry over unchanged.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Datelike, Duration, TimeZone, Utc};
use uuid::Uuid;

use super::episodic::InMemoryEpisodicStore;
use super::stores::{EpisodicMemoryEntry, PersonaState};
use crate::brain::BrainError;

/// Injectable clock — deterministic in tests, real time in production. `Send +
/// Sync` so a consolidator/summarizer can be shared across threads.
pub type ClockFn = Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>;

/// The default clock: the real UTC wall clock.
fn system_clock() -> ClockFn {
    Arc::new(Utc::now)
}

// ─────────────────────────────────────────────────────────────────────────────
// SleepKind + CoreMemoryKind
// ─────────────────────────────────────────────────────────────────────────────

/// Which tier of hierarchical consolidation a tick should run.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SleepKind {
    /// End-of-day: collapse the day's episodic entries into a DailyMemorySummary.
    Daily,
    /// End-of-week: cluster the week's daily summaries into semantic topic groups.
    Weekly,
    /// End-of-month: compute the persona delta and write a PersonaDeltaSnapshot.
    Monthly,
    /// Caller-initiated pass — runs whichever tiers have work pending.
    OnDemand,
}

/// Why a memory was promoted to the core tier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoreMemoryKind {
    /// A fact the user explicitly asked the AI to remember.
    UserAsserted,
    /// Inferred from interaction patterns — a long-standing preference / theme.
    PatternInferred,
    /// Promoted because of extreme salience.
    HighSalience,
    /// Promoted by the host directly (profile sync, identity bootstrap).
    HostProvided,
}

// ─────────────────────────────────────────────────────────────────────────────
// Tier records
// ─────────────────────────────────────────────────────────────────────────────

/// A core memory the AI will not forget. Compact by design.
#[derive(Debug, Clone, PartialEq)]
pub struct CoreMemory {
    /// Stable identifier.
    pub id: String,
    /// UTC time the memory was committed to core.
    pub created_at_utc: DateTime<Utc>,
    /// UTC time the memory was last reinforced (re-asserted, re-cited).
    pub last_reinforced_utc: DateTime<Utc>,
    /// Short, dense statement of the memory, third-person from the AI's view.
    pub statement: String,
    /// How the memory came to be in core.
    pub kind: CoreMemoryKind,
    /// Optional topic label (e.g. "family", "career", "health").
    pub topic: Option<String>,
    /// Embedding of the statement for retrieval; `None` when unavailable.
    pub embedding: Option<Vec<f32>>,
    /// How many times this memory has been reinforced.
    pub reinforcement_count: i32,
    /// Trace back to the lower-tier source memory, if one exists.
    pub source_memory_id: Option<String>,
}

/// Options for constructing a [`CoreMemory`] — mirrors the C# object-initializer
/// defaults. Fields left `None`/default map to the reference defaults.
#[derive(Default)]
pub struct CoreMemoryInit {
    pub statement: String,
    pub kind: Option<CoreMemoryKind>,
    pub topic: Option<String>,
    pub embedding: Option<Vec<f32>>,
    pub source_memory_id: Option<String>,
}

/// Builds a [`CoreMemory`] with C#-equivalent defaults (new id, now timestamps).
pub fn create_core_memory(init: CoreMemoryInit, clock: &ClockFn) -> CoreMemory {
    let now = clock();
    CoreMemory {
        id: new_id(),
        created_at_utc: now,
        last_reinforced_utc: now,
        statement: init.statement,
        kind: init.kind.unwrap_or(CoreMemoryKind::UserAsserted),
        topic: init.topic,
        embedding: init.embedding,
        reinforcement_count: 0,
        source_memory_id: init.source_memory_id,
    }
}

/// Compressed record of a single calendar day's worth of episodic memory.
/// (No `PartialEq`: `highlight_entries` holds `EpisodicMemoryEntry`, which does
/// not implement it. Tests compare the individual scalar fields instead.)
#[derive(Debug, Clone)]
pub struct DailyMemorySummary {
    /// Stable identifier.
    pub id: String,
    /// The calendar day this summary covers ("YYYY-MM-DD", UTC).
    pub day: String,
    /// UTC time the summary was produced.
    pub generated_at_utc: DateTime<Utc>,
    /// Short prose summary of the day's gist.
    pub summary: String,
    /// The most salient verbatim exchanges from the day (typically 3–5).
    pub highlight_entries: Vec<EpisodicMemoryEntry>,
    /// Total number of episodic entries collapsed into this summary.
    pub episode_count: usize,
    /// Aggregated topic weights across the day's exchanges (label → weight).
    pub topic_weights: HashMap<String, f64>,
    /// Mean cosine-distance dispersion of the day's embeddings (0..1).
    pub topic_dispersion: f64,
    /// Salience score 0.0–1.0 assigned by the summariser.
    pub salience: f64,
}

/// Init shape for a [`DailyMemorySummary`] — mirrors C# object-initializer defaults.
pub struct DailyMemorySummaryInit {
    pub day: String,
    pub summary: String,
    pub highlight_entries: Vec<EpisodicMemoryEntry>,
    pub episode_count: usize,
    pub topic_weights: HashMap<String, f64>,
    pub topic_dispersion: f64,
    pub salience: f64,
}

impl DailyMemorySummaryInit {
    /// A minimal init for the given day with every other field at its default.
    pub fn for_day(day: impl Into<String>) -> Self {
        Self {
            day: day.into(),
            summary: String::new(),
            highlight_entries: Vec::new(),
            episode_count: 0,
            topic_weights: HashMap::new(),
            topic_dispersion: 0.0,
            salience: 0.0,
        }
    }
}

/// Builds a [`DailyMemorySummary`] with C#-equivalent defaults.
pub fn create_daily_summary(init: DailyMemorySummaryInit, clock: &ClockFn) -> DailyMemorySummary {
    DailyMemorySummary {
        id: new_id(),
        day: init.day,
        generated_at_utc: clock(),
        summary: init.summary,
        highlight_entries: init.highlight_entries,
        episode_count: init.episode_count,
        topic_weights: init.topic_weights,
        topic_dispersion: init.topic_dispersion,
        salience: init.salience,
    }
}

/// Topic-coherent cluster of daily summaries — the "semantic memory" tier.
#[derive(Debug, Clone, PartialEq)]
pub struct SemanticMemoryCluster {
    /// Stable identifier.
    pub id: String,
    /// UTC time the cluster was produced.
    pub generated_at_utc: DateTime<Utc>,
    /// The week this cluster covers — Monday of that week ("YYYY-MM-DD", UTC).
    pub week_starting_monday: String,
    /// Dominant topic label for this cluster.
    pub topic: String,
    /// Short prose summary of the cluster's gist.
    pub summary: String,
    /// Centroid embedding (mean of constituent embeddings); `None` when unavailable.
    pub centroid_embedding: Option<Vec<f32>>,
    /// IDs of the daily summaries that contributed to this cluster.
    pub source_daily_ids: Vec<String>,
    /// Aggregate weight of the topic across constituent days.
    pub topic_weight: f64,
    /// Salience score 0.0–1.0.
    pub salience: f64,
}

/// Init shape for a [`SemanticMemoryCluster`] — mirrors C# object-initializer defaults.
pub struct SemanticMemoryClusterInit {
    pub week_starting_monday: String,
    pub topic: String,
    pub summary: String,
    pub centroid_embedding: Option<Vec<f32>>,
    pub source_daily_ids: Vec<String>,
    pub topic_weight: f64,
    pub salience: f64,
}

impl SemanticMemoryClusterInit {
    /// A minimal init for the given week + topic with every other field default.
    pub fn new(week_starting_monday: impl Into<String>, topic: impl Into<String>) -> Self {
        Self {
            week_starting_monday: week_starting_monday.into(),
            topic: topic.into(),
            summary: String::new(),
            centroid_embedding: None,
            source_daily_ids: Vec::new(),
            topic_weight: 0.0,
            salience: 0.0,
        }
    }
}

/// Builds a [`SemanticMemoryCluster`] with C#-equivalent defaults.
pub fn create_semantic_cluster(
    init: SemanticMemoryClusterInit,
    clock: &ClockFn,
) -> SemanticMemoryCluster {
    SemanticMemoryCluster {
        id: new_id(),
        generated_at_utc: clock(),
        week_starting_monday: init.week_starting_monday,
        topic: init.topic,
        summary: init.summary,
        centroid_embedding: init.centroid_embedding,
        source_daily_ids: init.source_daily_ids,
        topic_weight: init.topic_weight,
        salience: init.salience,
    }
}

/// Diff between a PersonaState at the start and end of a consolidation period.
#[derive(Debug, Clone, PartialEq)]
pub struct PersonaDeltaSnapshot {
    /// Stable identifier.
    pub id: String,
    /// UTC time the delta was captured.
    pub generated_at_utc: DateTime<Utc>,
    /// Start of the period ("YYYY-MM-DD", UTC).
    pub period_start: String,
    /// End of the period ("YYYY-MM-DD", UTC).
    pub period_end: String,
    /// User identifier.
    pub user_id: String,
    /// Verbosity at period start.
    pub verbosity_before: String,
    /// Verbosity at period end.
    pub verbosity_after: String,
    /// Formality at period start.
    pub formality_before: String,
    /// Formality at period end.
    pub formality_after: String,
    /// New topics that emerged in the period (label → accumulated weight).
    pub new_topics: HashMap<String, f64>,
    /// Topics that gained the most weight (label → weight delta).
    pub strengthened_topics: HashMap<String, f64>,
    /// Topics the user explicitly down-voted during the period.
    pub newly_disfavoured_topics: Vec<String>,
    /// Net positive minus negative signals across the period.
    pub net_signal_delta: i32,
    /// Total interactions during the period.
    pub interactions_in_period: i32,
    /// Short human-readable narrative of how the persona changed.
    pub narrative: String,
}

/// Init shape for a [`PersonaDeltaSnapshot`] — mirrors C# object-initializer defaults.
pub struct PersonaDeltaSnapshotInit {
    pub period_start: String,
    pub period_end: String,
    pub user_id: String,
    pub verbosity_before: String,
    pub verbosity_after: String,
    pub formality_before: String,
    pub formality_after: String,
    pub new_topics: HashMap<String, f64>,
    pub strengthened_topics: HashMap<String, f64>,
    pub newly_disfavoured_topics: Vec<String>,
    pub net_signal_delta: i32,
    pub interactions_in_period: i32,
    pub narrative: String,
}

/// Builds a [`PersonaDeltaSnapshot`] with C#-equivalent defaults.
pub fn create_persona_delta(init: PersonaDeltaSnapshotInit, clock: &ClockFn) -> PersonaDeltaSnapshot {
    PersonaDeltaSnapshot {
        id: new_id(),
        generated_at_utc: clock(),
        period_start: init.period_start,
        period_end: init.period_end,
        user_id: init.user_id,
        verbosity_before: init.verbosity_before,
        verbosity_after: init.verbosity_after,
        formality_before: init.formality_before,
        formality_after: init.formality_after,
        new_topics: init.new_topics,
        strengthened_topics: init.strengthened_topics,
        newly_disfavoured_topics: init.newly_disfavoured_topics,
        net_signal_delta: init.net_signal_delta,
        interactions_in_period: init.interactions_in_period,
        narrative: init.narrative,
    }
}

/// Outcome of a single consolidator tick.
#[derive(Debug, Clone, PartialEq)]
pub struct ConsolidationOutcome {
    pub kind: SleepKind,
    pub daily_summaries_produced: usize,
    pub semantic_clusters_produced: usize,
    pub persona_deltas_produced: usize,
    pub core_promotions: usize,
    pub episodes_pruned: usize,
    pub dailies_pruned: usize,
    pub semantics_pruned: usize,
    pub ran_at_utc: DateTime<Utc>,
}

/// Retention windows + core-promotion thresholds.
#[derive(Debug, Clone, Copy)]
pub struct MemoryConsolidationOptions {
    /// Days of episodic entries to retain after they've been summarised.
    pub episodic_retention_days: i64,
    /// Days of daily summaries to retain after weekly consolidation.
    pub daily_retention_days: i64,
    /// Days of semantic clusters to retain.
    pub semantic_retention_days: i64,
    /// Salience threshold above which daily summaries promote to core.
    pub daily_core_promotion_threshold: f64,
    /// Salience threshold above which weekly clusters promote to core.
    pub weekly_core_promotion_threshold: f64,
}

impl Default for MemoryConsolidationOptions {
    /// Defaults matching MemoryConsolidationOptions in the C# reference.
    fn default() -> Self {
        Self {
            episodic_retention_days: 7,
            daily_retention_days: 30,
            semantic_retention_days: 365,
            daily_core_promotion_threshold: 0.80,
            weekly_core_promotion_threshold: 0.75,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Day helpers — "YYYY-MM-DD" UTC date arithmetic
// ─────────────────────────────────────────────────────────────────────────────

/// UTC calendar day of a `DateTime<Utc>`, as "YYYY-MM-DD".
pub fn day_key_of(date: &DateTime<Utc>) -> String {
    format!(
        "{:04}-{:02}-{:02}",
        date.year(),
        date.month(),
        date.day()
    )
}

/// Parses a "YYYY-MM-DD" key back into a UTC `DateTime` at midnight.
fn parse_day_key(day: &str) -> DateTime<Utc> {
    let mut it = day.split('-');
    let y: i32 = it.next().and_then(|s| s.parse().ok()).unwrap_or(1970);
    let m: u32 = it.next().and_then(|s| s.parse().ok()).unwrap_or(1);
    let d: u32 = it.next().and_then(|s| s.parse().ok()).unwrap_or(1);
    Utc.with_ymd_and_hms(y, m, d, 0, 0, 0).single().unwrap_or_else(|| {
        Utc.with_ymd_and_hms(1970, 1, 1, 0, 0, 0).unwrap()
    })
}

/// Adds `days` (may be negative) to a "YYYY-MM-DD" key.
pub fn add_days(day: &str, days: i64) -> String {
    let dt = parse_day_key(day) + Duration::days(days);
    day_key_of(&dt)
}

/// The Monday of the week containing `day`. Monday = d minus ((dow+6)%7) days
/// (Sunday=0), matching the C#/TS reference.
pub fn monday_of(day: &str) -> String {
    // chrono weekday: Mon=0..Sun=6 via num_days_from_monday. The reference uses
    // Sun=0..Sat=6 then delta=(dow+6)%7, which is exactly num_days_from_monday.
    let delta = parse_day_key(day).weekday().num_days_from_monday() as i64;
    add_days(day, -delta)
}

/// Four-digit year of a "YYYY-MM-DD" key.
pub fn year_of(day: &str) -> i32 {
    parse_day_key(day).year()
}

/// 1-based month of a "YYYY-MM-DD" key.
pub fn month_of(day: &str) -> u32 {
    parse_day_key(day).month()
}

/// First day of the month containing `day`, as "YYYY-MM-DD".
pub fn month_first_day_of(day: &str) -> String {
    format!("{:04}-{:02}-01", year_of(day), month_of(day))
}

// ─────────────────────────────────────────────────────────────────────────────
// Cosine — FULL cosine (differs from the episodic store's dot-only cosine).
// ─────────────────────────────────────────────────────────────────────────────

/// Full cosine similarity: dot / (‖a‖·‖b‖). Returns 0 on a length mismatch or a
/// near-zero denominator. This does NOT assume the vectors are L2-normalised, so
/// it differs from the episodic store's dot-product cosine — both are kept.
pub fn cosine_full(a: &[f32], b: &[f32]) -> f64 {
    if a.len() != b.len() {
        return 0.0;
    }
    let mut dot = 0.0f64;
    let mut mag_a = 0.0f64;
    let mut mag_b = 0.0f64;
    for i in 0..a.len() {
        let ai = a[i] as f64;
        let bi = b[i] as f64;
        dot += ai * bi;
        mag_a += ai * ai;
        mag_b += bi * bi;
    }
    let denom = mag_a.sqrt() * mag_b.sqrt();
    if denom < f64::EPSILON {
        0.0
    } else {
        dot / denom
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Store traits + in-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/// The episodic source the consolidator reads and prunes. Mirrors the subset of
/// the episodic store the C#/TS consolidator uses (get-recent + prune). `&self`
/// (interior mutability) so the store can be shared behind an `Arc`, exactly like
/// [`super::episodic::EpisodicSearch`] does for recall.
pub trait EpisodicConsolidationSource: Send + Sync {
    /// Returns the most recent `count` entries, newest-first.
    fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, BrainError>;
    /// Removes entries recorded strictly before `cutoff`; returns count removed.
    fn prune_older_than(&self, cutoff: &DateTime<Utc>) -> Result<usize, BrainError>;
}

impl EpisodicConsolidationSource for InMemoryEpisodicStore {
    fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        self.get_recent_shared(count)
    }
    fn prune_older_than(&self, cutoff: &DateTime<Utc>) -> Result<usize, BrainError> {
        self.prune_older_than_shared(cutoff)
    }
}

/// Persistent store for tier-2 daily summaries. `&self` (interior mutability).
pub trait IDailyMemoryStore: Send + Sync {
    /// Adds a daily summary. Replaces any existing entry for the same day.
    fn upsert(&self, summary: DailyMemorySummary) -> Result<(), BrainError>;
    /// Returns the summary for the given day, or `None`.
    fn get(&self, day: &str) -> Result<Option<DailyMemorySummary>, BrainError>;
    /// Returns all summaries between from/to inclusive (day-ordered).
    fn get_range(
        &self,
        from_inclusive: &str,
        to_inclusive: &str,
    ) -> Result<Vec<DailyMemorySummary>, BrainError>;
    /// Removes summaries whose day is before cutoff. Returns count removed.
    fn prune_older_than(&self, cutoff: &str) -> Result<usize, BrainError>;
    /// Total summaries currently stored.
    fn count(&self) -> Result<usize, BrainError>;
}

/// Persistent store for tier-3 semantic memory clusters.
pub trait ISemanticMemoryStore: Send + Sync {
    /// Adds a cluster.
    fn add(&self, cluster: SemanticMemoryCluster) -> Result<(), BrainError>;
    /// Returns all clusters for the given week, ordered by topicWeight desc.
    fn get_week(&self, week_starting_monday: &str) -> Result<Vec<SemanticMemoryCluster>, BrainError>;
    /// Top-topK clusters by centroid cosine; recency fallback when query is None.
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<SemanticMemoryCluster>, BrainError>;
    /// Removes clusters whose week start is before cutoff.
    fn prune_older_than(&self, cutoff: &str) -> Result<usize, BrainError>;
    /// Total clusters currently stored.
    fn count(&self) -> Result<usize, BrainError>;
}

/// Persistent store for tier-4 persona-delta snapshots. Retained forever.
pub trait IPersonaDeltaStore: Send + Sync {
    /// Adds a delta snapshot.
    fn add(&self, snapshot: PersonaDeltaSnapshot) -> Result<(), BrainError>;
    /// Returns all snapshots for the given user, ordered by periodStart.
    fn get_for_user(&self, user_id: &str) -> Result<Vec<PersonaDeltaSnapshot>, BrainError>;
    /// Total snapshots currently stored.
    fn count(&self) -> Result<usize, BrainError>;
}

/// Persistent store for tier-5 core memories — things the AI will not forget.
pub trait ICoreMemoryStore: Send + Sync {
    /// Adds a core memory.
    fn add(&self, memory: CoreMemory) -> Result<(), BrainError>;
    /// Returns a core memory by id, or `None`.
    fn get(&self, id: &str) -> Result<Option<CoreMemory>, BrainError>;
    /// Top-topK core memories by embedding cosine; reinforcement fallback when None.
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<CoreMemory>, BrainError>;
    /// All core memories in reinforcement order (most reinforced first).
    fn list_all(&self) -> Result<Vec<CoreMemory>, BrainError>;
    /// Increments reinforcementCount and bumps lastReinforcedUtc. No-op when unknown.
    fn reinforce(&self, id: &str) -> Result<(), BrainError>;
    /// Removes a core memory.
    fn remove(&self, id: &str) -> Result<bool, BrainError>;
    /// Total core memories currently stored.
    fn count(&self) -> Result<usize, BrainError>;
}

/// Persona load/save the consolidator needs. Mirrors the TS `IPersonaStore`
/// (`load` returns a fresh default when none stored). `&self` interior mutability.
pub trait PersonaConsolidationStore: Send + Sync {
    /// Loads the persona for `user_id`, or a fresh default stamped with that id.
    fn load(&self, user_id: &str) -> Result<PersonaState, BrainError>;
    /// Persists the persona.
    fn save(&self, persona: &PersonaState) -> Result<(), BrainError>;
}

/// In-memory [`IDailyMemoryStore`].
#[derive(Debug, Default)]
pub struct InMemoryDailyMemoryStore {
    store: Mutex<HashMap<String, DailyMemorySummary>>,
}

impl InMemoryDailyMemoryStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl IDailyMemoryStore for InMemoryDailyMemoryStore {
    fn upsert(&self, summary: DailyMemorySummary) -> Result<(), BrainError> {
        self.store.lock().unwrap().insert(summary.day.clone(), summary);
        Ok(())
    }

    fn get(&self, day: &str) -> Result<Option<DailyMemorySummary>, BrainError> {
        Ok(self.store.lock().unwrap().get(day).cloned())
    }

    fn get_range(
        &self,
        from_inclusive: &str,
        to_inclusive: &str,
    ) -> Result<Vec<DailyMemorySummary>, BrainError> {
        let store = self.store.lock().unwrap();
        let mut out: Vec<DailyMemorySummary> = store
            .values()
            .filter(|s| s.day.as_str() >= from_inclusive && s.day.as_str() <= to_inclusive)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.day.cmp(&b.day));
        Ok(out)
    }

    fn prune_older_than(&self, cutoff: &str) -> Result<usize, BrainError> {
        let mut store = self.store.lock().unwrap();
        let before = store.len();
        store.retain(|d, _| d.as_str() >= cutoff);
        Ok(before - store.len())
    }

    fn count(&self) -> Result<usize, BrainError> {
        Ok(self.store.lock().unwrap().len())
    }
}

/// In-memory [`ISemanticMemoryStore`].
#[derive(Debug, Default)]
pub struct InMemorySemanticMemoryStore {
    store: Mutex<Vec<SemanticMemoryCluster>>,
}

impl InMemorySemanticMemoryStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ISemanticMemoryStore for InMemorySemanticMemoryStore {
    fn add(&self, cluster: SemanticMemoryCluster) -> Result<(), BrainError> {
        self.store.lock().unwrap().push(cluster);
        Ok(())
    }

    fn get_week(&self, week_starting_monday: &str) -> Result<Vec<SemanticMemoryCluster>, BrainError> {
        let store = self.store.lock().unwrap();
        let mut out: Vec<SemanticMemoryCluster> = store
            .iter()
            .filter(|c| c.week_starting_monday == week_starting_monday)
            .cloned()
            .collect();
        // topicWeight desc (stable — mirrors TS sort by (b.topicWeight - a.topicWeight)).
        out.sort_by(|a, b| {
            b.topic_weight
                .partial_cmp(&a.topic_weight)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        Ok(out)
    }

    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<SemanticMemoryCluster>, BrainError> {
        let store = self.store.lock().unwrap();
        match query_embedding {
            None => {
                let mut out: Vec<SemanticMemoryCluster> = store.clone();
                out.sort_by(|a, b| b.generated_at_utc.cmp(&a.generated_at_utc));
                out.truncate(top_k);
                Ok(out)
            }
            Some(q) => {
                let mut scored: Vec<(SemanticMemoryCluster, f64)> = store
                    .iter()
                    .filter_map(|c| {
                        c.centroid_embedding
                            .as_ref()
                            .map(|emb| (c.clone(), cosine_full(q, emb)))
                    })
                    .collect();
                scored.sort_by(|a, b| {
                    b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal)
                });
                scored.truncate(top_k);
                Ok(scored.into_iter().map(|(c, _)| c).collect())
            }
        }
    }

    fn prune_older_than(&self, cutoff: &str) -> Result<usize, BrainError> {
        let mut store = self.store.lock().unwrap();
        let before = store.len();
        store.retain(|c| c.week_starting_monday.as_str() >= cutoff);
        Ok(before - store.len())
    }

    fn count(&self) -> Result<usize, BrainError> {
        Ok(self.store.lock().unwrap().len())
    }
}

/// In-memory [`IPersonaDeltaStore`].
#[derive(Debug, Default)]
pub struct InMemoryPersonaDeltaStore {
    store: Mutex<Vec<PersonaDeltaSnapshot>>,
}

impl InMemoryPersonaDeltaStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl IPersonaDeltaStore for InMemoryPersonaDeltaStore {
    fn add(&self, snapshot: PersonaDeltaSnapshot) -> Result<(), BrainError> {
        self.store.lock().unwrap().push(snapshot);
        Ok(())
    }

    fn get_for_user(&self, user_id: &str) -> Result<Vec<PersonaDeltaSnapshot>, BrainError> {
        let store = self.store.lock().unwrap();
        let mut out: Vec<PersonaDeltaSnapshot> = store
            .iter()
            .filter(|s| s.user_id == user_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| a.period_start.cmp(&b.period_start));
        Ok(out)
    }

    fn count(&self) -> Result<usize, BrainError> {
        Ok(self.store.lock().unwrap().len())
    }
}

/// In-memory [`ICoreMemoryStore`].
#[derive(Debug, Default)]
pub struct InMemoryCoreMemoryStore {
    store: Mutex<HashMap<String, CoreMemory>>,
}

impl InMemoryCoreMemoryStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ICoreMemoryStore for InMemoryCoreMemoryStore {
    fn add(&self, memory: CoreMemory) -> Result<(), BrainError> {
        self.store.lock().unwrap().insert(memory.id.clone(), memory);
        Ok(())
    }

    fn get(&self, id: &str) -> Result<Option<CoreMemory>, BrainError> {
        Ok(self.store.lock().unwrap().get(id).cloned())
    }

    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<CoreMemory>, BrainError> {
        let store = self.store.lock().unwrap();
        match query_embedding {
            None => {
                let mut out: Vec<CoreMemory> = store.values().cloned().collect();
                out.sort_by(by_reinforcement);
                out.truncate(top_k);
                Ok(out)
            }
            Some(q) => {
                let mut scored: Vec<(CoreMemory, f64)> = store
                    .values()
                    .filter_map(|m| {
                        m.embedding
                            .as_ref()
                            .map(|emb| (m.clone(), cosine_full(q, emb)))
                    })
                    .collect();
                scored.sort_by(|a, b| {
                    b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal)
                });
                scored.truncate(top_k);
                Ok(scored.into_iter().map(|(m, _)| m).collect())
            }
        }
    }

    fn list_all(&self) -> Result<Vec<CoreMemory>, BrainError> {
        let store = self.store.lock().unwrap();
        let mut out: Vec<CoreMemory> = store.values().cloned().collect();
        out.sort_by(by_reinforcement);
        Ok(out)
    }

    fn reinforce(&self, id: &str) -> Result<(), BrainError> {
        let mut store = self.store.lock().unwrap();
        if let Some(m) = store.get_mut(id) {
            m.reinforcement_count += 1;
            m.last_reinforced_utc = Utc::now();
        }
        Ok(())
    }

    fn remove(&self, id: &str) -> Result<bool, BrainError> {
        Ok(self.store.lock().unwrap().remove(id).is_some())
    }

    fn count(&self) -> Result<usize, BrainError> {
        Ok(self.store.lock().unwrap().len())
    }
}

/// Sort: reinforcementCount desc, then lastReinforcedUtc desc.
fn by_reinforcement(a: &CoreMemory, b: &CoreMemory) -> std::cmp::Ordering {
    if b.reinforcement_count != a.reinforcement_count {
        b.reinforcement_count.cmp(&a.reinforcement_count)
    } else {
        b.last_reinforced_utc.cmp(&a.last_reinforced_utc)
    }
}

/// In-memory [`PersonaConsolidationStore`]. Keyed by userId; [`load`](Self::load)
/// returns a fresh default [`PersonaState`] (stamped with the requested userId)
/// when no persona has been persisted for that user.
#[derive(Debug, Default)]
pub struct InMemoryPersonaStore {
    store: Mutex<HashMap<String, PersonaState>>,
}

impl InMemoryPersonaStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl PersonaConsolidationStore for InMemoryPersonaStore {
    fn load(&self, user_id: &str) -> Result<PersonaState, BrainError> {
        let store = self.store.lock().unwrap();
        match store.get(user_id) {
            Some(p) => Ok(p.clone()),
            None => Ok(PersonaState::new(user_id)),
        }
    }

    fn save(&self, persona: &PersonaState) -> Result<(), BrainError> {
        self.store
            .lock()
            .unwrap()
            .insert(persona.user_id.clone(), persona.clone());
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMemorySummarizer + HeuristicSummarizer
// ─────────────────────────────────────────────────────────────────────────────

/// Produces the text + scores for each consolidation tier.
pub trait IMemorySummarizer: Send + Sync {
    /// Produces a DailyMemorySummary from the day's episodic entries.
    fn summarize_day(
        &self,
        day: &str,
        entries: &[EpisodicMemoryEntry],
    ) -> Result<DailyMemorySummary, BrainError>;
    /// Produces zero or more SemanticMemoryCluster records from a week's dailies.
    fn consolidate_week(
        &self,
        week_starting_monday: &str,
        days_in_week: &[DailyMemorySummary],
    ) -> Result<Vec<SemanticMemoryCluster>, BrainError>;
    /// Computes the PersonaDeltaSnapshot across the period.
    fn derive_persona_delta(
        &self,
        before: &PersonaState,
        after: &PersonaState,
        days_in_period: &[DailyMemorySummary],
    ) -> Result<PersonaDeltaSnapshot, BrainError>;
}

/// Heuristic [`IMemorySummarizer`] that requires no LLM. Produces summaries
/// entirely from structural signals — embedding clustering, topic-weight
/// aggregation, length-and-recency salience. Formulas are identical to the C#
/// HeuristicSummarizer.
pub struct HeuristicSummarizer {
    /// Max high-salience verbatim entries kept per DailyMemorySummary.
    pub highlight_count: usize,
    /// Min contributing days a topic needs across a week to form a cluster.
    pub min_days_per_topic_for_cluster: usize,
    clock: ClockFn,
}

impl Default for HeuristicSummarizer {
    fn default() -> Self {
        Self {
            highlight_count: 5,
            min_days_per_topic_for_cluster: 2,
            clock: system_clock(),
        }
    }
}

impl HeuristicSummarizer {
    /// Creates a summarizer with the default tuning (highlightCount 5,
    /// minDaysPerTopic 2) and the real clock.
    pub fn new() -> Self {
        Self::default()
    }

    /// Creates a summarizer with an injected clock (deterministic tests).
    pub fn with_clock(clock: ClockFn) -> Self {
        Self {
            clock,
            ..Self::default()
        }
    }

    /// Creates a summarizer with explicit tuning + clock.
    pub fn tuned(highlight_count: usize, min_days_per_topic_for_cluster: usize, clock: ClockFn) -> Self {
        Self {
            highlight_count,
            min_days_per_topic_for_cluster,
            clock,
        }
    }
}

impl IMemorySummarizer for HeuristicSummarizer {
    // ── summarize_day ─────────────────────────────────────────────────────────
    fn summarize_day(
        &self,
        day: &str,
        entries: &[EpisodicMemoryEntry],
    ) -> Result<DailyMemorySummary, BrainError> {
        if entries.is_empty() {
            let mut init = DailyMemorySummaryInit::for_day(day);
            init.summary = format!("No exchanges recorded on {day}.");
            init.episode_count = 0;
            return Ok(create_daily_summary(init, &self.clock));
        }

        let topic_weights = aggregate_topic_weights(entries);
        let dispersion = mean_pairwise_cosine_distance(entries);
        let highlights = select_highlights(entries, self.highlight_count);
        let salience = compute_daily_salience(entries.len(), &topic_weights, dispersion);
        let summary = build_daily_summary_text(day, entries.len(), &topic_weights, &highlights);

        Ok(create_daily_summary(
            DailyMemorySummaryInit {
                day: day.to_string(),
                summary,
                highlight_entries: highlights,
                episode_count: entries.len(),
                topic_weights,
                topic_dispersion: dispersion,
                salience,
            },
            &self.clock,
        ))
    }

    // ── consolidate_week ──────────────────────────────────────────────────────
    fn consolidate_week(
        &self,
        week_starting_monday: &str,
        days_in_week: &[DailyMemorySummary],
    ) -> Result<Vec<SemanticMemoryCluster>, BrainError> {
        if days_in_week.is_empty() {
            return Ok(Vec::new());
        }

        // Tally how many days each topic appeared in and its cumulative weight.
        // Topic labels arrive already lowercased from aggregate_topic_weights.
        let mut topic_to_days: HashMap<String, Vec<DailyMemorySummary>> = HashMap::new();
        let mut topic_to_weight: HashMap<String, f64> = HashMap::new();

        for d in days_in_week {
            for (topic, w) in &d.topic_weights {
                topic_to_days.entry(topic.clone()).or_default().push(d.clone());
                *topic_to_weight.entry(topic.clone()).or_insert(0.0) += *w;
            }
        }

        let mut total_weight: f64 = topic_to_weight.values().sum();
        if total_weight <= 0.0 {
            total_weight = 1.0;
        }

        // Iterate topics by weight desc (mirrors TS OrderByDescending); ties keyed
        // by topic for deterministic output.
        let mut topics_by_weight_desc: Vec<String> = topic_to_weight.keys().cloned().collect();
        topics_by_weight_desc.sort_by(|a, b| {
            let wa = topic_to_weight[a];
            let wb = topic_to_weight[b];
            match wb.partial_cmp(&wa).unwrap_or(std::cmp::Ordering::Equal) {
                std::cmp::Ordering::Equal => a.cmp(b),
                other => other,
            }
        });

        let mut clusters: Vec<SemanticMemoryCluster> = Vec::new();
        for topic in topics_by_weight_desc {
            let contributing_days = &topic_to_days[&topic];
            if contributing_days.len() < self.min_days_per_topic_for_cluster {
                continue;
            }

            let centroid = centroid_of_highlights(contributing_days);
            let weight = topic_to_weight[&topic];
            let cluster_salience =
                (weight / total_weight + (contributing_days.len() as f64 / 7.0) * 0.25).min(1.0);

            clusters.push(create_semantic_cluster(
                SemanticMemoryClusterInit {
                    week_starting_monday: week_starting_monday.to_string(),
                    topic: topic.clone(),
                    summary: build_weekly_cluster_text(&topic, contributing_days),
                    centroid_embedding: centroid,
                    source_daily_ids: contributing_days.iter().map(|d| d.id.clone()).collect(),
                    topic_weight: weight,
                    salience: cluster_salience,
                },
                &self.clock,
            ));
        }
        Ok(clusters)
    }

    // ── derive_persona_delta ──────────────────────────────────────────────────
    fn derive_persona_delta(
        &self,
        before: &PersonaState,
        after: &PersonaState,
        days_in_period: &[DailyMemorySummary],
    ) -> Result<PersonaDeltaSnapshot, BrainError> {
        let mut new_topics: HashMap<String, f64> = HashMap::new();
        let mut strengthened: HashMap<String, f64> = HashMap::new();
        for (topic, after_w) in &after.topic_weights {
            let before_w = before.topic_weights.get(topic).copied().unwrap_or(0.0);
            let after_w = *after_w;
            let delta = after_w - before_w;
            if before_w <= 0.0 && after_w > 0.0 {
                new_topics.insert(topic.clone(), after_w as f64);
            } else if delta > 0.0 {
                strengthened.insert(topic.clone(), delta as f64);
            }
        }

        let disfavoured_new: Vec<String> = after
            .disfavoured_topics
            .iter()
            .filter(|t| !before.disfavoured_topics.contains(*t))
            .cloned()
            .collect();

        let net_signals = after.positive_signals - before.positive_signals
            - (after.negative_signals - before.negative_signals);
        let interactions = after.total_interactions - before.total_interactions;

        let (period_start, period_end) = if !days_in_period.is_empty() {
            (min_day(days_in_period), max_day(days_in_period))
        } else {
            let k = day_key_of(&after.last_updated_at);
            (k.clone(), k)
        };

        let narrative = build_persona_narrative(
            before,
            after,
            &new_topics,
            &strengthened,
            &disfavoured_new,
            net_signals,
            interactions,
            &period_start,
            &period_end,
        );

        Ok(create_persona_delta(
            PersonaDeltaSnapshotInit {
                user_id: after.user_id.clone(),
                period_start,
                period_end,
                verbosity_before: before.verbosity.clone(),
                verbosity_after: after.verbosity.clone(),
                formality_before: before.formality.clone(),
                formality_after: after.formality.clone(),
                new_topics,
                strengthened_topics: strengthened,
                newly_disfavoured_topics: disfavoured_new,
                net_signal_delta: net_signals,
                interactions_in_period: interactions,
                narrative,
            },
            &self.clock,
        ))
    }
}

// ── Summarizer helpers — topic + dispersion ─────────────────────────────────

/// Topic weights from "topic" (+1) and pipe-split "topics" (each +1), lowercased.
fn aggregate_topic_weights(entries: &[EpisodicMemoryEntry]) -> HashMap<String, f64> {
    let mut weights: HashMap<String, f64> = HashMap::new();
    for e in entries {
        let tags = match &e.tags {
            Some(t) => t,
            None => continue,
        };
        if let Some(t) = tags.get("topic") {
            if !t.trim().is_empty() {
                accumulate_topic(&mut weights, t, 1.0);
            }
        }
        if let Some(multi) = tags.get("topics") {
            if !multi.trim().is_empty() {
                for p in multi.split('|') {
                    if p.is_empty() {
                        continue; // RemoveEmptyEntries
                    }
                    accumulate_topic(&mut weights, p, 1.0);
                }
            }
        }
    }
    weights
}

fn accumulate_topic(dict: &mut HashMap<String, f64>, topic: &str, weight: f64) {
    let key = topic.trim().to_lowercase();
    if key.is_empty() {
        return;
    }
    *dict.entry(key).or_insert(0.0) += weight;
}

/// Mean over all pairs of (1 - clamp(fullCosine,-1,1)); 0 when <2 embedded entries.
fn mean_pairwise_cosine_distance(entries: &[EpisodicMemoryEntry]) -> f64 {
    let with_embeddings: Vec<&EpisodicMemoryEntry> =
        entries.iter().filter(|e| has_embedding(e)).collect();
    if with_embeddings.len() < 2 {
        return 0.0;
    }

    let mut total = 0.0f64;
    let mut pairs = 0usize;
    for i in 0..with_embeddings.len() {
        for j in (i + 1)..with_embeddings.len() {
            let sim = cosine_full(
                with_embeddings[i].embedding.as_ref().unwrap(),
                with_embeddings[j].embedding.as_ref().unwrap(),
            );
            total += 1.0 - clamp(sim, -1.0, 1.0);
            pairs += 1;
        }
    }
    if pairs == 0 {
        0.0
    } else {
        clamp(total / pairs as f64, 0.0, 1.0)
    }
}

/// Top-`count` entries by salience proxy (or all when ≤count), re-sorted by time.
fn select_highlights(entries: &[EpisodicMemoryEntry], count: usize) -> Vec<EpisodicMemoryEntry> {
    if entries.len() <= count {
        let mut out: Vec<EpisodicMemoryEntry> = entries.to_vec();
        out.sort_by(by_time_asc);
        return out;
    }
    let mut scored: Vec<(EpisodicMemoryEntry, f64)> = entries
        .iter()
        .map(|e| (e.clone(), entry_salience_proxy(e, entries)))
        .collect();
    // OrderByDescending(score).ThenByDescending(recordedAt)
    scored.sort_by(|a, b| {
        match b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal) {
            std::cmp::Ordering::Equal => b.0.recorded_at_utc.cmp(&a.0.recorded_at_utc),
            other => other,
        }
    });
    scored.truncate(count);
    let mut out: Vec<EpisodicMemoryEntry> = scored.into_iter().map(|(e, _)| e).collect();
    out.sort_by(by_time_asc);
    out
}

fn entry_salience_proxy(entry: &EpisodicMemoryEntry, all: &[EpisodicMemoryEntry]) -> f64 {
    let length_score = clamp_upper(
        (entry.user_text.chars().count() + entry.assistant_text.chars().count()) as f64 / 800.0,
        1.0,
    );
    let mut uniqueness_score = 0.5;
    if has_embedding(entry) {
        let others: Vec<&EpisodicMemoryEntry> = all
            .iter()
            .filter(|e| e.id != entry.id && has_embedding(e))
            .collect();
        if !others.is_empty() {
            let mut sum = 0.0f64;
            for e in &others {
                sum += cosine_full(
                    entry.embedding.as_ref().unwrap(),
                    e.embedding.as_ref().unwrap(),
                );
            }
            let mean_sim = sum / others.len() as f64;
            uniqueness_score = 1.0 - clamp(mean_sim, -1.0, 1.0);
        }
    }
    length_score * 0.6 + uniqueness_score * 0.4
}

/// Daily salience = volume·0.4 + dispersion·0.3 + topicConcentration·0.3.
fn compute_daily_salience(
    episode_count: usize,
    topic_weights: &HashMap<String, f64>,
    dispersion: f64,
) -> f64 {
    let volume_score = clamp_upper(episode_count as f64 / 30.0, 1.0);
    let topic_concentration = if topic_weights.is_empty() {
        0.5
    } else {
        let mut max_w = f64::NEG_INFINITY;
        let mut sum_w = 0.0f64;
        for w in topic_weights.values() {
            if *w > max_w {
                max_w = *w;
            }
            sum_w += *w;
        }
        clamp_upper(max_w / sum_w.max(1.0), 1.0)
    };
    volume_score * 0.4 + dispersion * 0.3 + topic_concentration * 0.3
}

/// Mean of all highlight embeddings across contributing days; `None` when none.
fn centroid_of_highlights(days: &[DailyMemorySummary]) -> Option<Vec<f32>> {
    let mut all_embeddings: Vec<&Vec<f32>> = Vec::new();
    for d in days {
        for e in &d.highlight_entries {
            if has_embedding(e) {
                all_embeddings.push(e.embedding.as_ref().unwrap());
            }
        }
    }
    if all_embeddings.is_empty() {
        return None;
    }
    let dim = all_embeddings[0].len();
    let mut centroid = vec![0.0f32; dim];
    for e in &all_embeddings {
        let n = dim.min(e.len());
        for i in 0..n {
            centroid[i] += e[i];
        }
    }
    let count = all_embeddings.len() as f32;
    for c in centroid.iter_mut() {
        *c /= count;
    }
    Some(centroid)
}

// ── Summarizer helpers — text builders ──────────────────────────────────────

fn build_daily_summary_text(
    day: &str,
    count: usize,
    topics: &HashMap<String, f64>,
    highlights: &[EpisodicMemoryEntry],
) -> String {
    let top_topics = top_n_keys(topics, 3);
    let topics_clause = if !top_topics.is_empty() {
        format!(" Top topics: {}.", top_topics.join(", "))
    } else {
        String::new()
    };

    let highlight_clause = if !highlights.is_empty() {
        format!(
            " Standout moment: \"{}\".",
            truncate(&highlights[0].user_text, 120)
        )
    } else {
        String::new()
    };

    let exchanges = if count == 1 { "exchange." } else { "exchanges." };
    format!("On {day} you had {count} {exchanges}{topics_clause}{highlight_clause}")
}

fn build_weekly_cluster_text(topic: &str, contributing_days: &[DailyMemorySummary]) -> String {
    let total_episodes: usize = contributing_days.iter().map(|d| d.episode_count).sum();
    format!(
        "Across {} days this week you returned to \"{topic}\" — {total_episodes} exchanges in total.",
        contributing_days.len()
    )
}

#[allow(clippy::too_many_arguments)]
fn build_persona_narrative(
    before: &PersonaState,
    after: &PersonaState,
    new_topics: &HashMap<String, f64>,
    strengthened: &HashMap<String, f64>,
    disfavoured: &[String],
    net_signals: i32,
    interactions: i32,
    period_start: &str,
    period_end: &str,
) -> String {
    let mut parts: Vec<String> = Vec::new();
    parts.push(format!(
        "Between {period_start} and {period_end}, {interactions} interactions were recorded."
    ));
    if !new_topics.is_empty() {
        parts.push(format!(
            "New interests appeared: {}.",
            top_n_keys(new_topics, 3).join(", ")
        ));
    }
    if !strengthened.is_empty() {
        parts.push(format!(
            "Existing interests deepened around {}.",
            top_n_keys(strengthened, 3).join(", ")
        ));
    }
    if !disfavoured.is_empty() {
        parts.push(format!("Topics now avoided: {}.", disfavoured.join(", ")));
    }
    if before.verbosity != after.verbosity {
        parts.push(format!(
            "Preferred verbosity shifted from {} to {}.",
            before.verbosity, after.verbosity
        ));
    }
    if before.formality != after.formality {
        parts.push(format!(
            "Preferred tone shifted from {} to {}.",
            before.formality, after.formality
        ));
    }
    if net_signals != 0 {
        parts.push(if net_signals > 0 {
            format!("Net feedback was positive (+{net_signals}).")
        } else {
            format!("Net feedback was negative ({net_signals}).")
        });
    }
    parts.join(" ")
}

/// Keys of `map` ordered by value desc, top-n. Ties keyed by the label for
/// deterministic order (mirrors the reference's stable sort on inserted pairs).
fn top_n_keys(map: &HashMap<String, f64>, n: usize) -> Vec<String> {
    let mut entries: Vec<(&String, &f64)> = map.iter().collect();
    entries.sort_by(|a, b| {
        match b.1.partial_cmp(a.1).unwrap_or(std::cmp::Ordering::Equal) {
            std::cmp::Ordering::Equal => a.0.cmp(b.0),
            other => other,
        }
    });
    entries.into_iter().take(n).map(|(k, _)| k.clone()).collect()
}

fn truncate(s: &str, max: usize) -> String {
    if s.is_empty() {
        return String::new();
    }
    let chars: Vec<char> = s.chars().collect();
    if chars.len() <= max {
        return s.to_string();
    }
    let sliced: String = chars[..max].iter().collect();
    format!("{}…", sliced.trim_end())
}

// ── Shared small helpers ────────────────────────────────────────────────────

fn has_embedding(e: &EpisodicMemoryEntry) -> bool {
    match &e.embedding {
        Some(v) => !v.is_empty(),
        None => false,
    }
}

fn clamp(x: f64, lo: f64, hi: f64) -> f64 {
    x.max(lo).min(hi)
}

fn clamp_upper(x: f64, hi: f64) -> f64 {
    x.min(hi)
}

fn by_time_asc(a: &EpisodicMemoryEntry, b: &EpisodicMemoryEntry) -> std::cmp::Ordering {
    a.recorded_at_utc.cmp(&b.recorded_at_utc)
}

fn min_day(days: &[DailyMemorySummary]) -> String {
    let mut m = days[0].day.clone();
    for d in days {
        if d.day < m {
            m = d.day.clone();
        }
    }
    m
}

fn max_day(days: &[DailyMemorySummary]) -> String {
    let mut m = days[0].day.clone();
    for d in days {
        if d.day > m {
            m = d.day.clone();
        }
    }
    m
}

/// A fresh id. Uses UUID v4 (the crate only ships the v4 feature) — matching the
/// reference's `crypto.randomUUID()` / `Guid.NewGuid()`.
fn new_id() -> String {
    Uuid::new_v4().to_string()
}

// ─────────────────────────────────────────────────────────────────────────────
// IMemoryConsolidator + MemoryConsolidator
// ─────────────────────────────────────────────────────────────────────────────

/// Promotes lower-tier memory into higher tiers and enforces retention.
pub trait IMemoryConsolidator {
    /// Runs the consolidation pass for the given kind. OnDemand runs every tier
    /// with work pending. Returns the breakdown of what was produced and pruned.
    fn tick(&self, kind: SleepKind) -> Result<ConsolidationOutcome, BrainError>;
}

/// Default [`IMemoryConsolidator`] implementation.
pub struct MemoryConsolidator {
    episodic: Arc<dyn EpisodicConsolidationSource>,
    daily: Arc<dyn IDailyMemoryStore>,
    semantic: Arc<dyn ISemanticMemoryStore>,
    persona_delta: Arc<dyn IPersonaDeltaStore>,
    core: Arc<dyn ICoreMemoryStore>,
    persona_store: Arc<dyn PersonaConsolidationStore>,
    summarizer: Arc<dyn IMemorySummarizer>,
    options: MemoryConsolidationOptions,
    clock: ClockFn,
    user_id: String,
}

impl MemoryConsolidator {
    /// Creates a consolidator. `options` of `None` uses the C# defaults; `clock`
    /// of `None` uses the real clock; `user_id` of `None` uses "default".
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        episodic: Arc<dyn EpisodicConsolidationSource>,
        daily: Arc<dyn IDailyMemoryStore>,
        semantic: Arc<dyn ISemanticMemoryStore>,
        persona_delta: Arc<dyn IPersonaDeltaStore>,
        core: Arc<dyn ICoreMemoryStore>,
        persona_store: Arc<dyn PersonaConsolidationStore>,
        summarizer: Arc<dyn IMemorySummarizer>,
        options: Option<MemoryConsolidationOptions>,
        clock: Option<ClockFn>,
        user_id: Option<String>,
    ) -> Result<Self, BrainError> {
        Ok(Self {
            episodic,
            daily,
            semantic,
            persona_delta,
            core,
            persona_store,
            summarizer,
            options: options.unwrap_or_default(),
            clock: clock.unwrap_or_else(system_clock),
            user_id: user_id.unwrap_or_else(|| "default".to_string()),
        })
    }
}

impl IMemoryConsolidator for MemoryConsolidator {
    fn tick(&self, kind: SleepKind) -> Result<ConsolidationOutcome, BrainError> {
        let now = (self.clock)();
        let mut dailies = 0;
        let mut clusters = 0;
        let mut deltas = 0;
        let mut core_promoted = 0;
        let mut episodes_pruned = 0;
        let mut dailies_pruned = 0;
        let mut semantics_pruned = 0;

        if kind == SleepKind::Daily || kind == SleepKind::OnDemand {
            let (produced, promoted_from_daily) = self.run_daily(&now)?;
            dailies = produced;
            core_promoted += promoted_from_daily;
            episodes_pruned += self.prune_episodic(&now)?;
        }

        if kind == SleepKind::Weekly || kind == SleepKind::OnDemand {
            let (produced, promoted_from_weekly) = self.run_weekly(&now)?;
            clusters = produced;
            core_promoted += promoted_from_weekly;
            dailies_pruned += self.prune_dailies(&now)?;
        }

        if kind == SleepKind::Monthly || kind == SleepKind::OnDemand {
            deltas = self.run_monthly(&now)?;
            semantics_pruned += self.prune_semantics(&now)?;
        }

        Ok(ConsolidationOutcome {
            kind,
            daily_summaries_produced: dailies,
            semantic_clusters_produced: clusters,
            persona_deltas_produced: deltas,
            core_promotions: core_promoted,
            episodes_pruned,
            dailies_pruned,
            semantics_pruned,
            ran_at_utc: now,
        })
    }
}

impl MemoryConsolidator {
    // ── Daily pass ─────────────────────────────────────────────────────────────
    fn run_daily(&self, now: &DateTime<Utc>) -> Result<(usize, usize), BrainError> {
        let recent = self.episodic.get_recent(usize::MAX)?;
        if recent.is_empty() {
            return Ok((0, 0));
        }

        // Group episodes by their calendar day (UTC).
        let today = day_key_of(now);
        let mut by_day: HashMap<String, Vec<EpisodicMemoryEntry>> = HashMap::new();
        for e in recent {
            let key = day_key_of(&e.recorded_at_utc);
            by_day.entry(key).or_default().push(e);
        }

        let mut produced = 0usize;
        let mut promoted = 0usize;
        // Deterministic day ordering.
        let mut days: Vec<String> = by_day.keys().cloned().collect();
        days.sort();
        for day in days {
            if !(day < today) {
                continue; // only fully completed days
            }
            let group = &by_day[&day];

            let existing = self.daily.get(&day)?;
            if let Some(ref ex) = existing {
                if ex.episode_count == group.len() {
                    continue; // idempotent skip — already consolidated this day
                }
            }

            let mut ordered = group.clone();
            ordered.sort_by(by_time_asc);
            let summary = self.summarizer.summarize_day(&day, &ordered)?;
            let salience = summary.salience;
            self.daily.upsert(summary.clone())?;
            produced += 1;

            if salience >= self.options.daily_core_promotion_threshold {
                promoted += self.promote_daily_to_core(&summary)?;
            }
        }
        Ok((produced, promoted))
    }

    // ── Weekly pass ────────────────────────────────────────────────────────────
    fn run_weekly(&self, now: &DateTime<Utc>) -> Result<(usize, usize), BrainError> {
        let today = day_key_of(now);
        let this_monday = monday_of(&today);
        let last_monday = add_days(&this_monday, -7);
        let last_sunday = add_days(&last_monday, 6);

        let last_week = self.daily.get_range(&last_monday, &last_sunday)?;
        if last_week.is_empty() {
            return Ok((0, 0));
        }

        // Idempotency: if we already have clusters for this week, skip.
        let existing = self.semantic.get_week(&last_monday)?;
        if !existing.is_empty() {
            return Ok((0, 0));
        }

        let clusters = self.summarizer.consolidate_week(&last_monday, &last_week)?;
        let count = clusters.len();
        let mut promoted = 0usize;
        for c in clusters {
            let salience = c.salience;
            let cluster = c.clone();
            self.semantic.add(c)?;
            if salience >= self.options.weekly_core_promotion_threshold {
                promoted += self.promote_cluster_to_core(&cluster)?;
            }
        }
        Ok((count, promoted))
    }

    // ── Monthly pass ───────────────────────────────────────────────────────────
    fn run_monthly(&self, now: &DateTime<Utc>) -> Result<usize, BrainError> {
        let today = day_key_of(now);
        // Consider the most recently completed full month.
        let first_of_this_month = month_first_day_of(&today);
        let last_month_end = add_days(&first_of_this_month, -1);
        let last_month_start = month_first_day_of(&last_month_end);

        // Idempotency: skip if we already have a delta whose PeriodStart falls in
        // the previous month (compared by month-year, not exact dates).
        let existing_deltas = self.persona_delta.get_for_user(&self.user_id)?;
        if existing_deltas.iter().any(|d| {
            year_of(&d.period_start) == year_of(&last_month_start)
                && month_of(&d.period_start) == month_of(&last_month_start)
        }) {
            return Ok(0);
        }

        let days = self.daily.get_range(&last_month_start, &last_month_end)?;
        if days.is_empty() {
            return Ok(0);
        }

        let after = self.persona_store.load(&self.user_id)?;

        // For "before", reconstruct from the most recent prior delta if one exists;
        // otherwise treat as a fresh persona.
        let mut priors: Vec<PersonaDeltaSnapshot> = existing_deltas
            .into_iter()
            .filter(|d| d.period_end < last_month_start)
            .collect();
        // OrderByDescending(periodEnd): newest end first.
        priors.sort_by(|a, b| b.period_end.cmp(&a.period_end));
        let before = match priors.first() {
            None => new_persona(&self.user_id),
            Some(prior) => reconstruct_persona_before(&after, &days, prior),
        };

        let delta = self.summarizer.derive_persona_delta(&before, &after, &days)?;
        self.persona_delta.add(delta)?;
        Ok(1)
    }

    // ── Core promotions ──────────────────────────────────────────────────────
    fn promote_daily_to_core(&self, summary: &DailyMemorySummary) -> Result<usize, BrainError> {
        // FirstOrDefault on TopicWeights.OrderByDescending — null Key when empty.
        let mut top_topic: Option<String> = None;
        let mut top_weight = f64::NEG_INFINITY;
        // Deterministic: ties broken by label so the "top" is stable.
        let mut pairs: Vec<(&String, &f64)> = summary.topic_weights.iter().collect();
        pairs.sort_by(|a, b| {
            match b.1.partial_cmp(a.1).unwrap_or(std::cmp::Ordering::Equal) {
                std::cmp::Ordering::Equal => a.0.cmp(b.0),
                other => other,
            }
        });
        if let Some((k, v)) = pairs.first() {
            top_weight = **v;
            top_topic = Some((*k).clone());
        }
        let _ = top_weight;

        let statement = match &top_topic {
            None => format!("On {} an unusually meaningful day was recorded.", summary.day),
            Some(t) => format!("\"{t}\" mattered enough on {} to be remembered.", summary.day),
        };

        let mut embedding: Option<Vec<f32>> = None;
        for h in &summary.highlight_entries {
            if let Some(emb) = &h.embedding {
                if !emb.is_empty() {
                    embedding = Some(emb.clone());
                    break;
                }
            }
        }

        let memory = create_core_memory(
            CoreMemoryInit {
                statement,
                kind: Some(CoreMemoryKind::HighSalience),
                topic: top_topic,
                embedding,
                source_memory_id: Some(summary.id.clone()),
            },
            &self.clock,
        );
        self.core.add(memory)?;
        Ok(1)
    }

    fn promote_cluster_to_core(&self, cluster: &SemanticMemoryCluster) -> Result<usize, BrainError> {
        let memory = create_core_memory(
            CoreMemoryInit {
                statement: format!(
                    "\"{}\" has been a recurring theme (week of {}).",
                    cluster.topic, cluster.week_starting_monday
                ),
                kind: Some(CoreMemoryKind::PatternInferred),
                topic: Some(cluster.topic.clone()),
                embedding: cluster.centroid_embedding.clone(),
                source_memory_id: Some(cluster.id.clone()),
            },
            &self.clock,
        );
        self.core.add(memory)?;
        Ok(1)
    }

    // ── Retention ────────────────────────────────────────────────────────────
    fn prune_episodic(&self, now: &DateTime<Utc>) -> Result<usize, BrainError> {
        let cutoff = *now - Duration::days(self.options.episodic_retention_days);
        self.episodic.prune_older_than(&cutoff)
    }

    fn prune_dailies(&self, now: &DateTime<Utc>) -> Result<usize, BrainError> {
        let cutoff = add_days(&day_key_of(now), -self.options.daily_retention_days);
        self.daily.prune_older_than(&cutoff)
    }

    fn prune_semantics(&self, now: &DateTime<Utc>) -> Result<usize, BrainError> {
        let cutoff = add_days(&day_key_of(now), -self.options.semantic_retention_days);
        self.semantic.prune_older_than(&cutoff)
    }
}

/// Approximates the persona at the start of the period by subtracting the
/// in-period gains from the current persona. Conservative — when in doubt it
/// shows no change. Faithful port of ReconstructPersonaBeforeAsync.
fn reconstruct_persona_before(
    after: &PersonaState,
    days_in_period: &[DailyMemorySummary],
    prior: &PersonaDeltaSnapshot,
) -> PersonaState {
    let mut before = PersonaState::new(&after.user_id);
    before.verbosity = prior.verbosity_after.clone();
    before.formality = prior.formality_after.clone();
    before.preferred_locale = after.preferred_locale.clone();
    let episode_sum: usize = days_in_period.iter().map(|d| d.episode_count).sum();
    before.total_interactions = after.total_interactions - episode_sum as i32;
    before.positive_signals =
        (after.positive_signals - clamp_positive(prior.net_signal_delta)).max(0);
    before.negative_signals = after.negative_signals;

    // Carry over topic weights minus the strongest in-period gains.
    before.topic_weights = HashMap::new();
    for (topic, w) in &after.topic_weights {
        let new_w = match prior.strengthened_topics.get(topic) {
            Some(delta) => (*w - *delta as f32).max(0.0),
            None => *w,
        };
        before.topic_weights.insert(topic.clone(), new_w);
    }
    before.disfavoured_topics = after.disfavoured_topics.clone();
    before
}

fn new_persona(user_id: &str) -> PersonaState {
    PersonaState::new(user_id)
}

fn clamp_positive(v: i32) -> i32 {
    if v < 0 {
        0
    } else {
        v
    }
}
