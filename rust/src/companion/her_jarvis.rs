//! her_jarvis.rs
//!
//! The HER/Jarvis-level companion contracts (`CircleAI.Companion.HerJarvis`).
//! Each is a small trait (keeping the C# `I`-name) plus a working, deterministic
//! in-memory implementation — a 1:1 sync port of `HerJarvisContracts.cs` +
//! `HerJarvisRealImplementations.cs`.
//!
//! Four contract families already live in dedicated modules and are re-exported
//! from here so this module presents the full HerJarvis surface:
//!   * [`IWorldModel`] + `FrequencyWorldModel` / `BayesianWorldModel` (world_model.rs)
//!   * [`ITheoryOfMind`] + `BeliefTrackerTheoryOfMind` (theory_of_mind.rs)
//!   * [`IInnerMonologue`] + `TemplateInnerMonologue` / `ReasoningLoopInnerMonologue`
//!     (inner_monologue.rs)
//!   * [`IPredictiveEngine`] + `HistogramPredictiveEngine` / `SequencePredictiveEngine`
//!     (predictive_engine.rs)
//!
//! Streaming contracts (C# `Channel` / `IAsyncEnumerable`) are modelled as an
//! in-memory buffer with `publish` + a `drain`ing `stream()` that snapshots the
//! currently-buffered items — deterministic and testable, matching the
//! reference's publish-then-read behaviour. Where C# binds native/ONNX/cloud, the
//! implementation is a real local one with the native piece injected behind a
//! closure (never a stub, never a panic-on-call).

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Duration, Utc};
use serde_json::Value;
use uuid::Uuid;

// Re-export the four already-ported contract families so the HerJarvis surface
// is complete from this one module.
pub use super::inner_monologue::{
    IInnerMonologue, ReasoningLoopInnerMonologue, SelfReflection, TemplateInnerMonologue,
};
pub use super::predictive_engine::{
    AnticipatedNeed, HistogramPredictiveEngine, IPredictiveEngine, SequencePredictiveEngine,
};
pub use super::theory_of_mind::{BeliefTrackerTheoryOfMind, ITheoryOfMind, OtherMindEstimate};
pub use super::world_model::{
    BayesianWorldModel, CausalPrediction, FrequencyWorldModel, IWorldModel,
};

// =====================================================================
// 1. AlwaysOnPresence — start/stop with a monotonic heartbeat counter.
// =====================================================================

/// Always-on background presence across all devices. `Send + Sync`.
pub trait IAlwaysOnPresence: Send + Sync {
    /// Whether the presence loop is currently running.
    fn is_running(&self) -> bool;
    /// Starts the presence loop. Idempotent.
    fn start(&self);
    /// Stops the presence loop. Idempotent.
    fn stop(&self);
}

/// Heartbeat presence: a start/stop flag plus a `beat`-driven counter. The C#
/// reference uses a `System.Threading.Timer`; the deterministic port exposes an
/// explicit [`HeartbeatAlwaysOnPresence::beat`] the host (or a test) ticks, so
/// the heartbeat count is reproducible rather than wall-clock dependent.
#[derive(Debug, Default)]
pub struct HeartbeatAlwaysOnPresence {
    inner: Mutex<HeartbeatInner>,
}

#[derive(Debug, Default)]
struct HeartbeatInner {
    running: bool,
    ticks: i64,
}

impl HeartbeatAlwaysOnPresence {
    /// Returns a stopped presence with a zero heartbeat count.
    pub fn new() -> Self {
        Self::default()
    }

    /// The number of heartbeats recorded since construction.
    pub fn heartbeats(&self) -> i64 {
        self.inner.lock().unwrap().ticks
    }

    /// Records one heartbeat. A no-op while stopped (matches the timer being
    /// disposed in the C# `StopAsync`).
    pub fn beat(&self) {
        let mut inner = self.inner.lock().unwrap();
        if inner.running {
            inner.ticks += 1;
        }
    }
}

impl IAlwaysOnPresence for HeartbeatAlwaysOnPresence {
    fn is_running(&self) -> bool {
        self.inner.lock().unwrap().running
    }

    fn start(&self) {
        let mut inner = self.inner.lock().unwrap();
        if inner.running {
            return;
        }
        inner.running = true;
        // The C# timer fires immediately (dueTime = Zero): count the first beat.
        inner.ticks += 1;
    }

    fn stop(&self) {
        self.inner.lock().unwrap().running = false;
    }
}

// =====================================================================
// 2. FusedPerception — publish/drain buffer of fused percepts.
// =====================================================================

/// A single fused perceptual frame (vision + audio + text + named sensors).
#[derive(Debug, Clone, PartialEq)]
pub struct FusedPercept {
    pub at: DateTime<Utc>,
    pub vision: Option<String>,
    pub audio: Option<String>,
    pub text: Option<String>,
    pub sensors: BTreeMap<String, f64>,
}

impl FusedPercept {
    pub fn new(
        at: DateTime<Utc>,
        vision: Option<String>,
        audio: Option<String>,
        text: Option<String>,
        sensors: BTreeMap<String, f64>,
    ) -> Self {
        Self {
            at,
            vision,
            audio,
            text,
            sensors,
        }
    }
}

/// Fused perceptual stream. `stream` drains everything published so far.
pub trait IFusedPerception: Send + Sync {
    /// Snapshots (and drains) every percept published since the last read.
    fn stream(&self) -> Vec<FusedPercept>;
}

/// Channel-style fused perception: a FIFO buffer with a `publish` hook. Mirrors
/// the C# `ChannelFusedPerception` — publish writes, the consumer drains.
#[derive(Debug, Default)]
pub struct ChannelFusedPerception {
    buffer: Mutex<VecDeque<FusedPercept>>,
}

impl ChannelFusedPerception {
    /// Returns an empty perception buffer.
    pub fn new() -> Self {
        Self::default()
    }

    /// Publishes a percept to the buffer.
    pub fn publish(&self, p: FusedPercept) {
        self.buffer.lock().unwrap().push_back(p);
    }
}

impl IFusedPerception for ChannelFusedPerception {
    fn stream(&self) -> Vec<FusedPercept> {
        self.buffer.lock().unwrap().drain(..).collect()
    }
}

// =====================================================================
// 3. IdentitySync — append-only delta log with a monotonic cursor.
// =====================================================================

/// Memory + identity sync across devices.
pub trait IIdentitySync: Send + Sync {
    /// Appends a delta (raw JSON) to the log.
    fn push(&self, delta_json: &str);
    /// Returns `{"cursor":N,"deltas":[...]}` for every delta after `since_cursor`.
    fn pull(&self, since_cursor: &str) -> String;
}

/// Append-only JSON delta log. Cursor is a monotonic 1-based counter; `pull`
/// concatenates every delta whose cursor is strictly greater than the supplied
/// one. Byte-identical output shape to the C# `JsonIdentitySync`.
#[derive(Debug, Default)]
pub struct JsonIdentitySync {
    inner: Mutex<IdentitySyncInner>,
}

#[derive(Debug, Default)]
struct IdentitySyncInner {
    log: Vec<(i64, String)>,
    next: i64,
}

impl JsonIdentitySync {
    /// Returns an empty log.
    pub fn new() -> Self {
        Self::default()
    }
}

impl IIdentitySync for JsonIdentitySync {
    fn push(&self, delta_json: &str) {
        let mut inner = self.inner.lock().unwrap();
        inner.next += 1;
        let cursor = inner.next;
        inner.log.push((cursor, delta_json.to_string()));
    }

    fn pull(&self, since_cursor: &str) -> String {
        let since: i64 = since_cursor.parse().unwrap_or(0);
        let inner = self.inner.lock().unwrap();
        let taken: Vec<&String> = inner
            .log
            .iter()
            .filter(|(c, _)| *c > since)
            .map(|(_, d)| d)
            .collect();
        let max_cursor = inner
            .log
            .iter()
            .filter(|(c, _)| *c > since)
            .map(|(c, _)| *c)
            .last()
            .unwrap_or(since);
        let mut payload = format!("{{\"cursor\":{max_cursor},\"deltas\":[");
        for (i, d) in taken.iter().enumerate() {
            if i > 0 {
                payload.push(',');
            }
            payload.push_str(d);
        }
        payload.push_str("]}");
        payload
    }
}

// =====================================================================
// 4. ContinuousLearner — exponentially weighted average reward per id.
// =====================================================================

/// Continuous online learning from interaction feedback.
pub trait IContinuousLearner: Send + Sync {
    /// Folds `reward` into the running EWA for `interaction_id`.
    ///
    /// # Panics
    /// Panics if `interaction_id` is blank (mirrors the C# `ArgumentException`).
    fn register_feedback(&self, interaction_id: &str, reward: f64, context_json: &str);
}

/// Exponentially-weighted-average continuous learner. A new observation folds in
/// at rate `alpha`; the first observation for an id seeds the average directly.
#[derive(Debug)]
pub struct EwaContinuousLearner {
    state: Mutex<HashMap<String, (f64, f64)>>, // id -> (avg, weight)
    alpha: f64,
}

impl Default for EwaContinuousLearner {
    fn default() -> Self {
        Self::new(0.2)
    }
}

impl EwaContinuousLearner {
    /// Creates a learner with the given fold-in rate `alpha` in `(0, 1]`.
    ///
    /// # Panics
    /// Panics if `alpha <= 0` or `alpha > 1`.
    pub fn new(alpha: f64) -> Self {
        assert!(alpha > 0.0 && alpha <= 1.0, "alpha out of range");
        Self {
            state: Mutex::new(HashMap::new()),
            alpha,
        }
    }

    /// The current average reward for `interaction_id`, if any observations exist.
    pub fn average_reward_of(&self, interaction_id: &str) -> Option<f64> {
        self.state
            .lock()
            .unwrap()
            .get(interaction_id)
            .map(|(avg, _)| *avg)
    }

    /// The number of observations folded into `interaction_id`.
    pub fn observations_of(&self, interaction_id: &str) -> i64 {
        self.state
            .lock()
            .unwrap()
            .get(interaction_id)
            .map(|(_, w)| *w as i64)
            .unwrap_or(0)
    }
}

impl IContinuousLearner for EwaContinuousLearner {
    fn register_feedback(&self, interaction_id: &str, reward: f64, _context_json: &str) {
        assert!(!interaction_id.trim().is_empty(), "interactionId required");
        let mut state = self.state.lock().unwrap();
        state
            .entry(interaction_id.to_string())
            .and_modify(|(avg, weight)| {
                *avg = *avg * (1.0 - self.alpha) + reward * self.alpha;
                *weight += 1.0;
            })
            .or_insert((reward, 1.0));
    }
}

// =====================================================================
// 6. GoalPursuer — store goal + milestones; replan recalculates the plan.
// =====================================================================

/// A multi-month goal with a serialised milestone plan and progress fraction.
#[derive(Debug, Clone, PartialEq)]
pub struct LongHorizonGoal {
    pub id: String,
    pub description: String,
    pub deadline_utc: DateTime<Utc>,
    pub plan_json: String,
    pub progress_fraction: f64,
}

impl LongHorizonGoal {
    pub fn new(
        id: impl Into<String>,
        description: impl Into<String>,
        deadline_utc: DateTime<Utc>,
        plan_json: impl Into<String>,
        progress_fraction: f64,
    ) -> Self {
        Self {
            id: id.into(),
            description: description.into(),
            deadline_utc,
            plan_json: plan_json.into(),
            progress_fraction,
        }
    }
}

/// Multi-month goal pursuit with replanning.
pub trait IGoalPursuer: Send + Sync {
    /// Registers a new goal with a future deadline and an auto-built plan.
    ///
    /// # Panics
    /// Panics if `description` is blank or `deadline_utc` is not in the future.
    fn register(&self, description: &str, deadline_utc: DateTime<Utc>) -> LongHorizonGoal;
    /// Returns the current state of goal `id`, if it exists.
    fn current(&self, id: &str) -> Option<LongHorizonGoal>;
    /// Rebuilds the milestone plan for goal `id` from "now" to its deadline.
    ///
    /// # Panics
    /// Panics if `id` is unknown (mirrors the C# `InvalidOperationException`).
    fn replan(&self, id: &str);
}

/// In-memory goal pursuer. The milestone plan divides the remaining time into
/// 2..=8 evenly-spaced milestones (one per ~14 days) as a JSON document.
#[derive(Debug, Default)]
pub struct InMemoryGoalPursuer {
    goals: Mutex<HashMap<String, LongHorizonGoal>>,
}

impl InMemoryGoalPursuer {
    /// Returns an empty pursuer.
    pub fn new() -> Self {
        Self::default()
    }

    /// Sets the progress fraction (`0.0..=1.0`) of goal `id`.
    ///
    /// # Panics
    /// Panics if `fraction` is out of range, or `id` is unknown.
    pub fn progress(&self, id: &str, fraction: f64) {
        assert!((0.0..=1.0).contains(&fraction), "fraction out of range");
        let mut goals = self.goals.lock().unwrap();
        let g = goals.get_mut(id).expect("Unknown goal");
        g.progress_fraction = fraction;
    }

    fn build_plan(description: &str, now: DateTime<Utc>, deadline_utc: DateTime<Utc>) -> String {
        let total_days = ((deadline_utc - now).num_days()).max(1);
        let milestones = (total_days / 14).clamp(2, 8);
        let span = deadline_utc - now;
        let mut sb = format!(
            "{{\"description\":{},\"milestones\":[",
            Value::String(description.to_string())
        );
        for i in 1..=milestones {
            if i > 1 {
                sb.push(',');
            }
            // step * i, computed in whole nanoseconds like C#'s TimeSpan division.
            let step_ns = span.num_nanoseconds().unwrap_or(0) / milestones;
            let due = now + Duration::nanoseconds(step_ns * i);
            sb.push_str(&format!(
                "{{\"index\":{},\"due\":\"{}\"}}",
                i,
                due.to_rfc3339_opts(chrono::SecondsFormat::AutoSi, true)
            ));
        }
        sb.push_str("]}");
        sb
    }
}

impl IGoalPursuer for InMemoryGoalPursuer {
    fn register(&self, description: &str, deadline_utc: DateTime<Utc>) -> LongHorizonGoal {
        assert!(!description.trim().is_empty(), "description required");
        let now = Utc::now();
        assert!(deadline_utc > now, "deadline must be in the future");
        let id = Uuid::new_v4().simple().to_string();
        let plan = Self::build_plan(description, now, deadline_utc);
        let g = LongHorizonGoal::new(&id, description, deadline_utc, plan, 0.0);
        self.goals.lock().unwrap().insert(id, g.clone());
        g
    }

    fn current(&self, id: &str) -> Option<LongHorizonGoal> {
        self.goals.lock().unwrap().get(id).cloned()
    }

    fn replan(&self, id: &str) {
        let mut goals = self.goals.lock().unwrap();
        let g = goals.get(id).expect("Unknown goal").clone();
        let plan = Self::build_plan(&g.description, Utc::now(), g.deadline_utc);
        goals.get_mut(id).unwrap().plan_json = plan;
    }
}

// =====================================================================
// 7. EpisodicMemory (HerJarvis) — TF-based similarity recall.
// =====================================================================

/// A lived-experience record: title + JSON content, timestamped.
#[derive(Debug, Clone, PartialEq)]
pub struct EpisodeRecord {
    pub id: String,
    pub at: DateTime<Utc>,
    pub title: String,
    pub content_json: String,
}

impl EpisodeRecord {
    pub fn new(
        id: impl Into<String>,
        at: DateTime<Utc>,
        title: impl Into<String>,
        content_json: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            at,
            title: title.into(),
            content_json: content_json.into(),
        }
    }
}

/// Episodic memory of lived experiences (HerJarvis contract).
pub trait IEpisodicMemory: Send + Sync {
    /// Records (or overwrites) an episode.
    ///
    /// # Panics
    /// Panics if the episode id is blank.
    fn record(&self, episode: EpisodeRecord);
    /// Returns up to `take` episodes most similar to `query` by term overlap.
    ///
    /// # Panics
    /// Panics if `take <= 0`.
    fn recall(&self, query: &str, take: usize) -> Vec<EpisodeRecord>;
}

/// Term-frequency episodic memory: overlap score = Σ q_tf · d_tf over shared
/// terms (case-insensitive, tokens ≥ 2 chars). Deterministic, in-memory.
#[derive(Debug, Default)]
pub struct TfEpisodicMemory {
    inner: Mutex<TfInner>,
}

#[derive(Debug, Default)]
struct TfInner {
    episodes: HashMap<String, EpisodeRecord>,
    terms: HashMap<String, HashMap<String, i64>>,
}

impl TfEpisodicMemory {
    /// Returns an empty store.
    pub fn new() -> Self {
        Self::default()
    }

    /// Splits `text` on non-alphanumeric runs, lower-cases, and counts tokens of
    /// length ≥ 2 — mirrors the C# `ToTermFrequency`.
    fn to_term_frequency(text: &str) -> HashMap<String, i64> {
        let mut d: HashMap<String, i64> = HashMap::new();
        let lowered = text.to_lowercase();
        for tok in lowered.split(|c: char| !c.is_ascii_alphanumeric()) {
            if tok.chars().count() >= 2 {
                *d.entry(tok.to_string()).or_insert(0) += 1;
            }
        }
        d
    }

    fn score(q: &HashMap<String, i64>, d: Option<&HashMap<String, i64>>) -> f64 {
        let Some(d) = d else { return 0.0 };
        let mut s = 0.0;
        for (k, qn) in q {
            if let Some(dn) = d.get(k) {
                s += (*qn as f64) * (*dn as f64);
            }
        }
        s
    }
}

impl IEpisodicMemory for TfEpisodicMemory {
    fn record(&self, episode: EpisodeRecord) {
        assert!(!episode.id.trim().is_empty(), "Id required");
        let terms = Self::to_term_frequency(&format!("{} {}", episode.title, episode.content_json));
        let mut inner = self.inner.lock().unwrap();
        inner.terms.insert(episode.id.clone(), terms);
        inner.episodes.insert(episode.id.clone(), episode);
    }

    fn recall(&self, query: &str, take: usize) -> Vec<EpisodeRecord> {
        assert!(take > 0, "take out of range");
        let q_terms = Self::to_term_frequency(query);
        if q_terms.is_empty() {
            return Vec::new();
        }
        let inner = self.inner.lock().unwrap();
        let mut scored: Vec<(EpisodeRecord, f64)> = inner
            .episodes
            .values()
            .map(|e| (e.clone(), Self::score(&q_terms, inner.terms.get(&e.id))))
            .filter(|(_, s)| *s > 0.0)
            .collect();
        scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        scored.into_iter().take(take).map(|(e, _)| e).collect()
    }
}

// =====================================================================
// 8. VoiceIdentity — mean-MFCC fingerprint + cosine similarity.
// =====================================================================

/// Per-user voice continuity: enroll then identify by voice fingerprint.
pub trait IVoiceIdentity: Send + Sync {
    /// Returns the enrolled user id whose fingerprint best matches the audio,
    /// or `None` if the best cosine similarity is `<= 0.85`.
    fn identify(&self, audio_pcm16: &[u8], sample_rate_hz: i32) -> Option<String>;
    /// Enrolls one audio sample as a reference fingerprint for `user_id`.
    ///
    /// # Panics
    /// Panics if `user_id` is blank.
    fn enroll(&self, user_id: &str, audio_pcm16: &[u8], sample_rate_hz: i32);
}

const MFCC_NUM_COEFFICIENTS: usize = 13;
const MFCC_NUM_MEL_FILTERS: usize = 26;
const MFCC_FRAME_SIZE: usize = 400; // 25 ms @ 16 kHz
const MFCC_FRAME_STEP: usize = 160; // 10 ms @ 16 kHz
const MFCC_PRE_EMPHASIS: f32 = 0.97;

/// Energy-band voice identity: the standard mean-MFCC baseline (pre-emphasis →
/// framing → Hamming → power spectrum → mel filterbank → log → DCT → mean),
/// matched by cosine similarity. A 1:1 port of the C# `EnergyBandVoiceIdentity`.
/// Real acoustics, no ML dependency; a production host can swap the ONNX speaker
/// model behind the same trait.
#[derive(Debug, Default)]
pub struct EnergyBandVoiceIdentity {
    enrolled: Mutex<HashMap<String, Vec<Vec<f64>>>>,
}

impl EnergyBandVoiceIdentity {
    /// Returns an empty enrolment store.
    pub fn new() -> Self {
        Self::default()
    }

    /// Mean MFCC vector across all frames of the PCM-16 buffer.
    fn mfcc(pcm16: &[u8], sample_rate_hz: i32) -> Vec<f64> {
        let mut samples = decode_pcm16(pcm16);
        if samples.len() < MFCC_FRAME_SIZE {
            return vec![0.0; MFCC_NUM_COEFFICIENTS];
        }
        pre_emphasis_filter(&mut samples);
        let filters = mel_filterbank(MFCC_NUM_MEL_FILTERS, MFCC_FRAME_SIZE, sample_rate_hz);
        let window = hamming_window(MFCC_FRAME_SIZE);

        let mut sum = vec![0.0f64; MFCC_NUM_COEFFICIENTS];
        let mut count = 0usize;
        let mut start = 0usize;
        while start + MFCC_FRAME_SIZE <= samples.len() {
            let mut frame = vec![0.0f32; MFCC_FRAME_SIZE];
            for i in 0..MFCC_FRAME_SIZE {
                frame[i] = samples[start + i] * window[i];
            }
            let power_spec = power_spectrum(&frame);
            let mel_energies = apply_filterbank(&power_spec, &filters);
            let mut log_energies = vec![0.0f64; MFCC_NUM_MEL_FILTERS];
            for i in 0..MFCC_NUM_MEL_FILTERS {
                log_energies[i] = mel_energies[i].max(1e-10).ln();
            }
            let coeffs = dct(&log_energies, MFCC_NUM_COEFFICIENTS);
            for i in 0..MFCC_NUM_COEFFICIENTS {
                sum[i] += coeffs[i];
            }
            count += 1;
            start += MFCC_FRAME_STEP;
        }
        if count == 0 {
            return sum;
        }
        for v in sum.iter_mut() {
            *v /= count as f64;
        }
        sum
    }
}

impl IVoiceIdentity for EnergyBandVoiceIdentity {
    fn enroll(&self, user_id: &str, audio_pcm16: &[u8], sample_rate_hz: i32) {
        assert!(!user_id.trim().is_empty(), "userId required");
        let fp = Self::mfcc(audio_pcm16, sample_rate_hz);
        self.enrolled
            .lock()
            .unwrap()
            .entry(user_id.to_string())
            .or_default()
            .push(fp);
    }

    fn identify(&self, audio_pcm16: &[u8], sample_rate_hz: i32) -> Option<String> {
        let fp = Self::mfcc(audio_pcm16, sample_rate_hz);
        let enrolled = self.enrolled.lock().unwrap();
        let mut best: Option<String> = None;
        let mut best_sim = -1.0f64;
        for (user, refs) in enrolled.iter() {
            for reference in refs {
                let sim = cosine_similarity_f64(&fp, reference);
                if sim > best_sim {
                    best_sim = sim;
                    best = Some(user.clone());
                }
            }
        }
        if best_sim > 0.85 {
            best
        } else {
            None
        }
    }
}

fn decode_pcm16(pcm16: &[u8]) -> Vec<f32> {
    let n = pcm16.len() / 2;
    let mut samples = vec![0.0f32; n];
    for i in 0..n {
        let s = (pcm16[i * 2] as i16) | ((pcm16[i * 2 + 1] as i16) << 8);
        samples[i] = s as f32 / 32768.0;
    }
    samples
}

fn pre_emphasis_filter(samples: &mut [f32]) {
    for i in (1..samples.len()).rev() {
        samples[i] -= MFCC_PRE_EMPHASIS * samples[i - 1];
    }
}

fn hamming_window(n: usize) -> Vec<f32> {
    let mut w = vec![0.0f32; n];
    for i in 0..n {
        w[i] = 0.54 - 0.46 * (2.0 * std::f32::consts::PI * i as f32 / (n as f32 - 1.0)).cos();
    }
    w
}

fn power_spectrum(frame: &[f32]) -> Vec<f64> {
    let n = frame.len();
    let half = n / 2 + 1;
    let mut spec = vec![0.0f64; half];
    for (k, spec_k) in spec.iter_mut().enumerate() {
        let mut re = 0.0f64;
        let mut im = 0.0f64;
        let omega = -2.0 * std::f64::consts::PI * k as f64 / n as f64;
        for (t, &sample) in frame.iter().enumerate() {
            re += sample as f64 * (omega * t as f64).cos();
            im += sample as f64 * (omega * t as f64).sin();
        }
        *spec_k = re * re + im * im;
    }
    spec
}

fn mel_filterbank(num_filters: usize, frame_size: usize, sample_rate_hz: i32) -> Vec<Vec<f64>> {
    fn hz_to_mel(hz: f64) -> f64 {
        2595.0 * (1.0 + hz / 700.0).log10()
    }
    fn mel_to_hz(mel: f64) -> f64 {
        700.0 * (10f64.powf(mel / 2595.0) - 1.0)
    }
    let low_mel = hz_to_mel(0.0);
    let high_mel = hz_to_mel(sample_rate_hz as f64 / 2.0);
    let points = num_filters + 2;
    let mut mel_points = vec![0.0f64; points];
    for i in 0..points {
        mel_points[i] = low_mel + (high_mel - low_mel) * i as f64 / (points as f64 - 1.0);
    }
    let mut bin_points = vec![0i64; points];
    for i in 0..points {
        bin_points[i] =
            ((frame_size as f64 + 1.0) * mel_to_hz(mel_points[i]) / sample_rate_hz as f64).floor()
                as i64;
    }

    let half = frame_size / 2 + 1;
    let mut filters = vec![vec![0.0f64; half]; num_filters];
    for m in 0..num_filters {
        let left = bin_points[m];
        let centre = bin_points[m + 1];
        let right = bin_points[m + 2];
        let mut k = left;
        while k < centre && (k as usize) < half {
            if centre != left {
                filters[m][k as usize] = (k - left) as f64 / (centre - left) as f64;
            }
            k += 1;
        }
        let mut k = centre;
        while k < right && (k as usize) < half {
            if right != centre {
                filters[m][k as usize] = (right - k) as f64 / (right - centre) as f64;
            }
            k += 1;
        }
    }
    filters
}

fn apply_filterbank(power_spec: &[f64], filters: &[Vec<f64>]) -> Vec<f64> {
    let mut energies = vec![0.0f64; filters.len()];
    for (m, filter) in filters.iter().enumerate() {
        let mut sum = 0.0f64;
        let len = power_spec.len().min(filter.len());
        for k in 0..len {
            sum += power_spec[k] * filter[k];
        }
        energies[m] = sum;
    }
    energies
}

fn dct(input: &[f64], num_coeffs: usize) -> Vec<f64> {
    let n = input.len();
    let mut output = vec![0.0f64; num_coeffs];
    for k in 0..num_coeffs {
        let mut sum = 0.0f64;
        for (i, &v) in input.iter().enumerate() {
            sum += v * (std::f64::consts::PI * k as f64 * (i as f64 + 0.5) / n as f64).cos();
        }
        output[k] = sum;
    }
    output
}

fn cosine_similarity_f64(a: &[f64], b: &[f64]) -> f64 {
    let mut dot = 0.0;
    let mut na = 0.0;
    let mut nb = 0.0;
    let n = a.len().min(b.len());
    for i in 0..n {
        dot += a[i] * b[i];
        na += a[i] * a[i];
        nb += b[i] * b[i];
    }
    if na == 0.0 || nb == 0.0 {
        0.0
    } else {
        dot / (na.sqrt() * nb.sqrt())
    }
}

// =====================================================================
// 9. CalibratedConfidence — history-nearest calibration with a band.
// =====================================================================

/// A confidence interval `[lower, upper]` in `[0, 1]`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ConfidenceBand {
    pub lower: f64,
    pub upper: f64,
}

impl ConfidenceBand {
    pub fn new(lower: f64, upper: f64) -> Self {
        Self { lower, upper }
    }
}

/// Calibrated uncertainty at orchestration.
pub trait ICalibratedConfidence: Send + Sync {
    /// Returns a calibrated confidence band for `answer` given `context_json`.
    ///
    /// # Panics
    /// Panics if `answer` handling requires it — the port never panics on a
    /// non-null string (C# only guards null, which Rust `&str` can't be).
    fn evaluate(&self, answer: &str, context_json: &str) -> ConfidenceBand;
}

/// Historical calibrated confidence: raw score from answer length + context
/// presence − hedge penalty, then (once ≥ 5 outcomes are recorded) calibrated to
/// the empirical correctness of the 5 nearest-by-raw-score outcomes. 1:1 with the
/// C# `HistoricalCalibratedConfidence`.
#[derive(Debug, Default)]
pub struct HistoricalCalibratedConfidence {
    history: Mutex<Vec<(f64, bool)>>,
}

impl HistoricalCalibratedConfidence {
    /// Returns an empty calibrator.
    pub fn new() -> Self {
        Self::default()
    }

    /// Records one (raw score, was-correct) outcome for future calibration.
    pub fn record_outcome(&self, raw_score: f64, was_correct: bool) {
        self.history
            .lock()
            .unwrap()
            .push((raw_score.clamp(0.0, 1.0), was_correct));
    }

    fn compute_raw_score(answer: &str, context_json: &str) -> f64 {
        let len = (answer.trim().chars().count() as f64).max(1.0);
        let hedges = count_hedges(answer);
        let hedge_penalty = (hedges as f64 * 0.1).min(0.5);
        let has_context = !context_json.trim().is_empty() && context_json.len() > 2;
        ((len.ln() / 10.0) + if has_context { 0.1 } else { 0.0 } - hedge_penalty).clamp(0.0, 1.0)
    }
}

impl ICalibratedConfidence for HistoricalCalibratedConfidence {
    fn evaluate(&self, answer: &str, context_json: &str) -> ConfidenceBand {
        let raw = Self::compute_raw_score(answer, context_json);
        let history = self.history.lock().unwrap();
        let calibrated = if history.len() < 5 {
            raw
        } else {
            let mut nearby: Vec<(f64, bool)> = history.clone();
            nearby.sort_by(|a, b| {
                (a.0 - raw)
                    .abs()
                    .partial_cmp(&(b.0 - raw).abs())
                    .unwrap_or(std::cmp::Ordering::Equal)
            });
            let take = &nearby[..5];
            take.iter().filter(|(_, c)| *c).count() as f64 / take.len() as f64
        };
        let half_band = (0.25 - calibrated * 0.2).max(0.05);
        ConfidenceBand::new(
            (calibrated - half_band).max(0.0),
            (calibrated + half_band).min(1.0),
        )
    }
}

/// Counts hedge words (`maybe|perhaps|might|possibly|unclear|don't know`),
/// whole-word, case-insensitive — the C# regex `\b(...)\b`.
fn count_hedges(text: &str) -> usize {
    const HEDGES: [&str; 5] = ["maybe", "perhaps", "might", "possibly", "unclear"];
    let lower = text.to_lowercase();
    let mut count = 0usize;
    // Whole-word tokens.
    for tok in lower.split(|c: char| !c.is_ascii_alphabetic() && c != '\'') {
        let t = tok.trim_matches('\'');
        if HEDGES.contains(&t) {
            count += 1;
        }
    }
    // The multi-word hedge "don't know".
    let collapsed: String = lower.split_whitespace().collect::<Vec<_>>().join(" ");
    count += collapsed.matches("don't know").count();
    count
}

// =====================================================================
// 11. EmotionSensor — keyword + arousal/valence inference.
// =====================================================================

/// An emotion label with arousal and valence in `[-1, 1]`.
#[derive(Debug, Clone, PartialEq)]
pub struct EmotionFrame {
    pub label: String,
    pub arousal: f64,
    pub valence: f64,
}

impl EmotionFrame {
    pub fn new(label: impl Into<String>, arousal: f64, valence: f64) -> Self {
        Self {
            label: label.into(),
            arousal,
            valence,
        }
    }
}

/// Emotion sensing from a fused JSON blob.
pub trait IEmotionSensor: Send + Sync {
    /// Infers the dominant emotion from keyword hits in `fused_json`.
    fn sense(&self, fused_json: &str) -> EmotionFrame;
}

/// One emotion pattern: label, arousal, valence, and its keyword set.
struct EmotionPattern {
    label: &'static str,
    arousal: f64,
    valence: f64,
    keywords: &'static [&'static str],
}

const EMOTION_PATTERNS: &[EmotionPattern] = &[
    EmotionPattern {
        label: "joy",
        arousal: 0.8,
        valence: 0.9,
        keywords: &["happy", "joy", "delight", "excited", "love", "wonderful"],
    },
    EmotionPattern {
        label: "anger",
        arousal: 0.9,
        valence: -0.8,
        keywords: &["angry", "furious", "rage", "hate", "annoyed"],
    },
    EmotionPattern {
        label: "sad",
        arousal: 0.3,
        valence: -0.7,
        keywords: &["sad", "lonely", "grief", "cry", "depressed", "down"],
    },
    EmotionPattern {
        label: "fear",
        arousal: 0.85,
        valence: -0.6,
        keywords: &["afraid", "scared", "terrified", "anxious", "worried"],
    },
    EmotionPattern {
        label: "surprise",
        arousal: 0.7,
        valence: 0.3,
        keywords: &["surprised", "amazed", "astonished", "wow"],
    },
    EmotionPattern {
        label: "calm",
        arousal: 0.1,
        valence: 0.5,
        keywords: &["calm", "peaceful", "relaxed", "content", "fine"],
    },
];

/// Keyword emotion sensor. Counts whole-word matches per pattern, then returns
/// the count-weighted mean arousal/valence and the top-count label. 1:1 with the
/// C# `KeywordEmotionSensor`.
#[derive(Debug, Default, Clone, Copy)]
pub struct KeywordEmotionSensor;

impl KeywordEmotionSensor {
    /// Returns a new sensor.
    pub fn new() -> Self {
        Self
    }
}

impl IEmotionSensor for KeywordEmotionSensor {
    fn sense(&self, fused_json: &str) -> EmotionFrame {
        let lower = fused_json.to_lowercase();
        let tokens: Vec<&str> = lower
            .split(|c: char| !c.is_ascii_alphanumeric())
            .filter(|s| !s.is_empty())
            .collect();
        let hits: Vec<(&EmotionPattern, usize)> = EMOTION_PATTERNS
            .iter()
            .map(|p| {
                let count = tokens
                    .iter()
                    .filter(|t| p.keywords.contains(*t))
                    .count();
                (p, count)
            })
            .filter(|(_, c)| *c > 0)
            .collect();
        if hits.is_empty() {
            return EmotionFrame::new("neutral", 0.0, 0.0);
        }
        let total_weight: usize = hits.iter().map(|(_, c)| *c).sum();
        let arousal =
            hits.iter().map(|(p, c)| p.arousal * *c as f64).sum::<f64>() / total_weight as f64;
        let valence =
            hits.iter().map(|(p, c)| p.valence * *c as f64).sum::<f64>() / total_weight as f64;
        // Top by count (stable → earliest pattern wins ties, matching C#
        // OrderByDescending's stable sort over the pattern order).
        let top = hits
            .iter()
            .max_by_key(|(_, c)| *c)
            .map(|(p, _)| p.label)
            .unwrap();
        EmotionFrame::new(top, arousal, valence)
    }
}

// =====================================================================
// 12. SkillAcquisition — demo store with name extraction.
// =====================================================================

/// A skill learned from a demonstration (kept as raw JSON).
#[derive(Debug, Clone, PartialEq)]
pub struct AcquiredSkill {
    pub id: String,
    pub name: String,
    pub description_json: String,
}

impl AcquiredSkill {
    pub fn new(
        id: impl Into<String>,
        name: impl Into<String>,
        description_json: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            name: name.into(),
            description_json: description_json.into(),
        }
    }
}

/// Skill acquisition from demonstrations.
pub trait ISkillAcquisition: Send + Sync {
    /// Stores a new skill, naming it from the demo's `name` field when present.
    fn acquire(&self, demonstration_json: &str) -> AcquiredSkill;
    /// Lists acquired skills, ordered by name.
    fn list(&self) -> Vec<AcquiredSkill>;
}

/// Demo-store skill acquisition. Extracts an optional `name` from the demo JSON,
/// otherwise falls back to `skill-<first6ofid>`. 1:1 with the C#
/// `DemoStoreSkillAcquisition`.
#[derive(Debug, Default)]
pub struct DemoStoreSkillAcquisition {
    skills: Mutex<HashMap<String, AcquiredSkill>>,
}

impl DemoStoreSkillAcquisition {
    /// Returns an empty store.
    pub fn new() -> Self {
        Self::default()
    }

    fn extract_name(demonstration_json: &str) -> Option<String> {
        let v: Value = serde_json::from_str(demonstration_json).ok()?;
        v.as_object()?
            .get("name")?
            .as_str()
            .map(|s| s.to_string())
    }
}

impl ISkillAcquisition for DemoStoreSkillAcquisition {
    fn acquire(&self, demonstration_json: &str) -> AcquiredSkill {
        let id = Uuid::new_v4().simple().to_string();
        let name =
            Self::extract_name(demonstration_json).unwrap_or_else(|| format!("skill-{}", &id[..6]));
        let skill = AcquiredSkill::new(&id, name, demonstration_json);
        self.skills.lock().unwrap().insert(id, skill.clone());
        skill
    }

    fn list(&self) -> Vec<AcquiredSkill> {
        let mut out: Vec<AcquiredSkill> =
            self.skills.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }
}

// =====================================================================
// 15. PersonalKnowledgeGraph — adjacency-list graph with relation kinds.
// =====================================================================

/// A knowledge-graph node (HerJarvis variant): id, kind, name, and properties.
#[derive(Debug, Clone, PartialEq)]
pub struct KnowledgeNode {
    pub id: String,
    pub kind: String,
    pub name: String,
    pub properties: BTreeMap<String, String>,
}

impl KnowledgeNode {
    pub fn new(
        id: impl Into<String>,
        kind: impl Into<String>,
        name: impl Into<String>,
        properties: BTreeMap<String, String>,
    ) -> Self {
        Self {
            id: id.into(),
            kind: kind.into(),
            name: name.into(),
            properties,
        }
    }
}

/// A directed, labelled relation between two nodes.
#[derive(Debug, Clone, PartialEq)]
pub struct KnowledgeRelation {
    pub from_id: String,
    pub to_id: String,
    pub relation: String,
}

impl KnowledgeRelation {
    pub fn new(
        from_id: impl Into<String>,
        to_id: impl Into<String>,
        relation: impl Into<String>,
    ) -> Self {
        Self {
            from_id: from_id.into(),
            to_id: to_id.into(),
            relation: relation.into(),
        }
    }
}

/// Personal knowledge graph (HerJarvis contract).
pub trait IPersonalKnowledgeGraph: Send + Sync {
    /// Inserts or replaces a node.
    ///
    /// # Panics
    /// Panics if the node id is blank.
    fn upsert_node(&self, node: KnowledgeNode);
    /// Inserts or replaces a relation (dedup on `(to_id, relation)`).
    fn upsert_relation(&self, rel: KnowledgeRelation);
    /// Returns the out-neighbour nodes of `id`.
    ///
    /// # Panics
    /// Panics if `id` is blank.
    fn neighbours(&self, id: &str) -> Vec<KnowledgeNode>;
}

/// Adjacency-list personal knowledge graph. 1:1 with the C#
/// `AdjacencyPersonalKnowledgeGraph`.
#[derive(Debug, Default)]
pub struct AdjacencyPersonalKnowledgeGraph {
    inner: Mutex<AdjacencyInner>,
}

#[derive(Debug, Default)]
struct AdjacencyInner {
    nodes: HashMap<String, KnowledgeNode>,
    out_edges: HashMap<String, Vec<KnowledgeRelation>>,
}

impl AdjacencyPersonalKnowledgeGraph {
    /// Returns an empty graph.
    pub fn new() -> Self {
        Self::default()
    }
}

impl IPersonalKnowledgeGraph for AdjacencyPersonalKnowledgeGraph {
    fn upsert_node(&self, node: KnowledgeNode) {
        assert!(!node.id.trim().is_empty(), "Id required");
        self.inner.lock().unwrap().nodes.insert(node.id.clone(), node);
    }

    fn upsert_relation(&self, rel: KnowledgeRelation) {
        let mut inner = self.inner.lock().unwrap();
        let list = inner.out_edges.entry(rel.from_id.clone()).or_default();
        list.retain(|r| !(r.to_id == rel.to_id && r.relation == rel.relation));
        list.push(rel);
    }

    fn neighbours(&self, id: &str) -> Vec<KnowledgeNode> {
        assert!(!id.trim().is_empty(), "id required");
        let inner = self.inner.lock().unwrap();
        let Some(rels) = inner.out_edges.get(id) else {
            return Vec::new();
        };
        rels.iter()
            .filter_map(|r| inner.nodes.get(&r.to_id).cloned())
            .collect()
    }
}

// =====================================================================
// 16. LiveWorldKnowledge — topic pub/sub broker.
// =====================================================================

/// A world fact tagged by topic.
#[derive(Debug, Clone, PartialEq)]
pub struct WorldFact {
    pub topic: String,
    pub summary_json: String,
    pub at: DateTime<Utc>,
}

impl WorldFact {
    pub fn new(topic: impl Into<String>, summary_json: impl Into<String>, at: DateTime<Utc>) -> Self {
        Self {
            topic: topic.into(),
            summary_json: summary_json.into(),
            at,
        }
    }
}

/// Live world-knowledge stream (topic pub/sub).
pub trait ILiveWorldKnowledge: Send + Sync {
    /// Drains every fact published to any of `topics` since the last read.
    fn subscribe(&self, topics: &[String]) -> Vec<WorldFact>;
}

/// Topic-keyed pub/sub broker. A fact is delivered only if a subscriber has
/// registered its topic (the C# `Publish` writes only to an existing channel);
/// `subscribe` both registers the topics and drains their buffers. 1:1 with the
/// C# `TopicLiveWorldKnowledge` (publish-then-read shape).
#[derive(Debug, Default)]
pub struct TopicLiveWorldKnowledge {
    by_topic: Mutex<HashMap<String, VecDeque<WorldFact>>>,
}

impl TopicLiveWorldKnowledge {
    /// Returns an empty broker.
    pub fn new() -> Self {
        Self::default()
    }

    /// Publishes a fact to subscribers of the matching topic (no-op if none).
    pub fn publish(&self, fact: WorldFact) {
        let mut by_topic = self.by_topic.lock().unwrap();
        if let Some(q) = by_topic.get_mut(&fact.topic) {
            q.push_back(fact);
        }
    }
}

impl ILiveWorldKnowledge for TopicLiveWorldKnowledge {
    fn subscribe(&self, topics: &[String]) -> Vec<WorldFact> {
        let mut by_topic = self.by_topic.lock().unwrap();
        let mut out = Vec::new();
        for t in topics {
            let q = by_topic.entry(t.clone()).or_default();
            out.extend(q.drain(..));
        }
        out
    }
}

// =====================================================================
// 17. BioSignalStream — publish/drain buffer of bio signals.
// =====================================================================

/// A single bio-signal reading.
#[derive(Debug, Clone, PartialEq)]
pub struct BioSignal {
    pub kind: String,
    pub value: f64,
    pub at: DateTime<Utc>,
}

impl BioSignal {
    pub fn new(kind: impl Into<String>, value: f64, at: DateTime<Utc>) -> Self {
        Self {
            kind: kind.into(),
            value,
            at,
        }
    }
}

/// Bio-signal integration stream.
pub trait IBioSignalStream: Send + Sync {
    /// Snapshots (and drains) every bio signal published since the last read.
    fn stream(&self) -> Vec<BioSignal>;
}

/// Fan-in bio-signal stream with a `publish` hook. 1:1 with the C#
/// `ChannelBioSignalStream`.
#[derive(Debug, Default)]
pub struct ChannelBioSignalStream {
    buffer: Mutex<VecDeque<BioSignal>>,
}

impl ChannelBioSignalStream {
    /// Returns an empty stream.
    pub fn new() -> Self {
        Self::default()
    }

    /// Publishes a bio signal to the buffer.
    pub fn publish(&self, s: BioSignal) {
        self.buffer.lock().unwrap().push_back(s);
    }
}

impl IBioSignalStream for ChannelBioSignalStream {
    fn stream(&self) -> Vec<BioSignal> {
        self.buffer.lock().unwrap().drain(..).collect()
    }
}

// =====================================================================
// 18. PhysicalActuator — device-handler registry with per-action dispatch.
// =====================================================================

/// A command to a physical device.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicalCommand {
    pub device_id: String,
    pub action: String,
    pub args: BTreeMap<String, String>,
}

impl PhysicalCommand {
    pub fn new(
        device_id: impl Into<String>,
        action: impl Into<String>,
        args: BTreeMap<String, String>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            action: action.into(),
            args,
        }
    }
}

/// The result of a physical command.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicalCommandResult {
    pub succeeded: bool,
    pub error: Option<String>,
}

impl PhysicalCommandResult {
    pub fn ok() -> Self {
        Self {
            succeeded: true,
            error: None,
        }
    }
    pub fn fail(error: impl Into<String>) -> Self {
        Self {
            succeeded: false,
            error: Some(error.into()),
        }
    }
}

/// A registered device handler.
pub type DeviceHandler = Arc<dyn Fn(&PhysicalCommand) -> PhysicalCommandResult + Send + Sync>;

/// Robotics / physical actuation.
pub trait IPhysicalActuator: Send + Sync {
    /// Dispatches `command` to its device handler, or fails for an unknown device.
    fn invoke(&self, command: &PhysicalCommand) -> PhysicalCommandResult;
}

/// Device-handler registry actuator. The native robotics binding is the injected
/// per-device closure; unknown devices fail cleanly. 1:1 with the C#
/// `RegistryPhysicalActuator`.
#[derive(Default, Clone)]
pub struct RegistryPhysicalActuator {
    handlers: Arc<Mutex<HashMap<String, DeviceHandler>>>,
}

impl RegistryPhysicalActuator {
    /// Returns an actuator with no devices registered.
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers a handler closure for `device_id`.
    ///
    /// # Panics
    /// Panics if `device_id` is blank.
    pub fn register_device(&self, device_id: &str, handler: DeviceHandler) {
        assert!(!device_id.trim().is_empty(), "deviceId required");
        self.handlers
            .lock()
            .unwrap()
            .insert(device_id.to_string(), handler);
    }
}

impl IPhysicalActuator for RegistryPhysicalActuator {
    fn invoke(&self, command: &PhysicalCommand) -> PhysicalCommandResult {
        let handlers = self.handlers.lock().unwrap();
        match handlers.get(&command.device_id) {
            Some(h) => h(command),
            None => PhysicalCommandResult::fail(format!(
                "Unknown device '{}'",
                command.device_id
            )),
        }
    }
}

// =====================================================================
// 19. AgentPeerNetwork — in-memory mailbox per agent id.
// =====================================================================

/// A message between two agents.
#[derive(Debug, Clone, PartialEq)]
pub struct AgentToAgentMessage {
    pub from_agent_id: String,
    pub to_agent_id: String,
    pub payload: String,
    pub at: DateTime<Utc>,
}

impl AgentToAgentMessage {
    pub fn new(
        from_agent_id: impl Into<String>,
        to_agent_id: impl Into<String>,
        payload: impl Into<String>,
        at: DateTime<Utc>,
    ) -> Self {
        Self {
            from_agent_id: from_agent_id.into(),
            to_agent_id: to_agent_id.into(),
            payload: payload.into(),
            at,
        }
    }
}

/// Agent-to-agent peer protocol.
pub trait IAgentPeerNetwork: Send + Sync {
    /// Delivers a message to the recipient's mailbox.
    fn send(&self, message: AgentToAgentMessage);
    /// Drains every message waiting in `for_agent_id`'s mailbox.
    ///
    /// # Panics
    /// Panics if `for_agent_id` is blank.
    fn receive(&self, for_agent_id: &str) -> Vec<AgentToAgentMessage>;
}

/// In-memory mailbox network. 1:1 with the C# `MailboxAgentPeerNetwork`.
#[derive(Debug, Default)]
pub struct MailboxAgentPeerNetwork {
    mailboxes: Mutex<HashMap<String, VecDeque<AgentToAgentMessage>>>,
}

impl MailboxAgentPeerNetwork {
    /// Returns an empty network.
    pub fn new() -> Self {
        Self::default()
    }
}

impl IAgentPeerNetwork for MailboxAgentPeerNetwork {
    fn send(&self, message: AgentToAgentMessage) {
        self.mailboxes
            .lock()
            .unwrap()
            .entry(message.to_agent_id.clone())
            .or_default()
            .push_back(message);
    }

    fn receive(&self, for_agent_id: &str) -> Vec<AgentToAgentMessage> {
        assert!(!for_agent_id.trim().is_empty(), "forAgentId required");
        self.mailboxes
            .lock()
            .unwrap()
            .entry(for_agent_id.to_string())
            .or_default()
            .drain(..)
            .collect()
    }
}

// =====================================================================
// 20. FederatedFineTuner — job runner with status tracking.
// =====================================================================

/// The status of a fine-tune job.
#[derive(Debug, Clone, PartialEq)]
pub struct FineTuneJobStatus {
    pub job_id: String,
    pub progress: f64,
    pub error: Option<String>,
}

impl FineTuneJobStatus {
    pub fn new(job_id: impl Into<String>, progress: f64, error: Option<String>) -> Self {
        Self {
            job_id: job_id.into(),
            progress,
            error,
        }
    }
}

/// The injected trainer: given `(base_model, training_data)`, returns the final
/// progress (`1.0` on success) or an error message.
pub type TrainerFn = Arc<dyn Fn(&str, &str) -> Result<f64, String> + Send + Sync>;

/// Federated / on-device fine-tune pipeline.
pub trait IFederatedFineTuner: Send + Sync {
    /// Runs a training job synchronously and returns its id.
    ///
    /// # Panics
    /// Panics if `base_model` or `training_data_path` is blank.
    fn start(&self, base_model: &str, training_data_path: &str) -> String;
    /// Returns the status of job `job_id`.
    fn status(&self, job_id: &str) -> FineTuneJobStatus;
}

/// In-memory federated fine-tuner. The C# reference runs the trainer on a
/// background task; the sync port runs it inline (deterministic) and records the
/// terminal status. The training step itself is the injected [`TrainerFn`], so
/// the MNN/LoRA plumbing stays outside. Default trainer reports completion.
#[derive(Clone)]
pub struct InMemoryFederatedFineTuner {
    jobs: Arc<Mutex<HashMap<String, FineTuneJobStatus>>>,
    trainer: TrainerFn,
}

impl Default for InMemoryFederatedFineTuner {
    fn default() -> Self {
        Self::new(None)
    }
}

impl InMemoryFederatedFineTuner {
    /// Creates a fine-tuner. `trainer` defaults to a no-op that reports success.
    pub fn new(trainer: Option<TrainerFn>) -> Self {
        let trainer = trainer.unwrap_or_else(|| Arc::new(|_, _| Ok(1.0)));
        Self {
            jobs: Arc::new(Mutex::new(HashMap::new())),
            trainer,
        }
    }
}

impl IFederatedFineTuner for InMemoryFederatedFineTuner {
    fn start(&self, base_model: &str, training_data_path: &str) -> String {
        assert!(!base_model.trim().is_empty(), "baseModel required");
        assert!(
            !training_data_path.trim().is_empty(),
            "trainingDataPath required"
        );
        let job_id = Uuid::new_v4().simple().to_string();
        let status = match (self.trainer)(base_model, training_data_path) {
            Ok(p) => FineTuneJobStatus::new(&job_id, p.clamp(0.0, 1.0), None),
            Err(e) => FineTuneJobStatus::new(&job_id, 0.0, Some(e)),
        };
        self.jobs.lock().unwrap().insert(job_id.clone(), status);
        job_id
    }

    fn status(&self, job_id: &str) -> FineTuneJobStatus {
        self.jobs
            .lock()
            .unwrap()
            .get(job_id)
            .cloned()
            .unwrap_or_else(|| FineTuneJobStatus::new(job_id, 0.0, Some("unknown job".to_string())))
    }
}

// =====================================================================
// 21. FirstTokenOptimizer — sliding-window p50 latency tracker.
// =====================================================================

/// The first-token latency budget: target vs current p50 (ms).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FirstTokenBudget {
    pub target_ms: i32,
    pub current_p50_ms: i32,
}

impl FirstTokenBudget {
    pub fn new(target_ms: i32, current_p50_ms: i32) -> Self {
        Self {
            target_ms,
            current_p50_ms,
        }
    }
}

/// Sub-100ms first-token latency tracking.
pub trait IFirstTokenOptimizer: Send + Sync {
    /// Returns the current budget (target + observed p50).
    fn current(&self) -> FirstTokenBudget;
}

/// Sliding-window p50 first-token optimizer. Keeps the last `window_size`
/// samples; p50 is `sorted[len/2]`. 1:1 with the C#
/// `SlidingP50FirstTokenOptimizer`.
#[derive(Debug)]
pub struct SlidingP50FirstTokenOptimizer {
    inner: Mutex<VecDeque<i32>>,
    window_size: usize,
    target_ms: i32,
}

impl Default for SlidingP50FirstTokenOptimizer {
    fn default() -> Self {
        Self::new(100, 256)
    }
}

impl SlidingP50FirstTokenOptimizer {
    /// Creates an optimizer with the given target and window size.
    ///
    /// # Panics
    /// Panics if `target_ms <= 0` or `window_size == 0`.
    pub fn new(target_ms: i32, window_size: usize) -> Self {
        assert!(target_ms > 0, "targetMs out of range");
        assert!(window_size > 0, "windowSize out of range");
        Self {
            inner: Mutex::new(VecDeque::new()),
            window_size,
            target_ms,
        }
    }

    /// Records one first-token latency sample.
    ///
    /// # Panics
    /// Panics if `ms < 0`.
    pub fn record_first_token_latency(&self, ms: i32) {
        assert!(ms >= 0, "ms out of range");
        let mut samples = self.inner.lock().unwrap();
        samples.push_back(ms);
        while samples.len() > self.window_size {
            samples.pop_front();
        }
    }
}

impl IFirstTokenOptimizer for SlidingP50FirstTokenOptimizer {
    fn current(&self) -> FirstTokenBudget {
        let samples = self.inner.lock().unwrap();
        let p50 = if samples.is_empty() {
            0
        } else {
            let mut sorted: Vec<i32> = samples.iter().copied().collect();
            sorted.sort_unstable();
            sorted[sorted.len() / 2]
        };
        FirstTokenBudget::new(self.target_ms, p50)
    }
}

// =====================================================================
// 22. CryptoDelegation — HMAC-SHA256 sign + verify (self-contained).
// =====================================================================

/// A cryptographic delegation credential.
#[derive(Debug, Clone, PartialEq)]
pub struct DelegationCredential {
    pub issuer: String,
    pub subject_id: String,
    pub scope: String,
    pub expires_at_utc: DateTime<Utc>,
    pub signature: String,
}

impl DelegationCredential {
    pub fn new(
        issuer: impl Into<String>,
        subject_id: impl Into<String>,
        scope: impl Into<String>,
        expires_at_utc: DateTime<Utc>,
        signature: impl Into<String>,
    ) -> Self {
        Self {
            issuer: issuer.into(),
            subject_id: subject_id.into(),
            scope: scope.into(),
            expires_at_utc,
            signature: signature.into(),
        }
    }
}

/// Cryptographic delegation framework.
pub trait ICryptoDelegation: Send + Sync {
    /// Issues a credential for `subject_id`/`scope` valid for `lifetime`.
    ///
    /// # Panics
    /// Panics if `subject_id`/`scope` is blank or `lifetime <= 0`.
    fn issue(&self, subject_id: &str, scope: &str, lifetime: Duration) -> DelegationCredential;
    /// Verifies issuer, expiry, and signature of `credential`.
    fn verify(&self, credential: &DelegationCredential) -> bool;
}

/// HMAC-SHA256 delegation. The C# reference signs with ECDSA P-256; Rust's core
/// carries no asymmetric-crypto crate, so the port uses a self-contained
/// HMAC-SHA256 over the same canonical payload — a real, verifiable MAC with the
/// secret key injected at construction. A host that needs public-key delegation
/// swaps this behind the same [`ICryptoDelegation`] trait.
pub struct HmacCryptoDelegation {
    key: Vec<u8>,
    issuer: String,
}

impl HmacCryptoDelegation {
    /// Creates a delegation signer.
    ///
    /// # Panics
    /// Panics if `issuer` is blank.
    pub fn new(issuer: &str, key: Vec<u8>) -> Self {
        assert!(!issuer.trim().is_empty(), "issuer required");
        // A random per-instance key when none is supplied (still verifiable
        // within the process, matching the ephemeral-key C# default).
        let key = if key.is_empty() {
            Uuid::new_v4().as_bytes().to_vec()
        } else {
            key
        };
        Self {
            key,
            issuer: issuer.to_string(),
        }
    }

    /// Creates a signer with the default issuer `"circleai-companion"` and an
    /// ephemeral key.
    pub fn with_default_issuer() -> Self {
        Self::new("circleai-companion", Vec::new())
    }

    fn canonical(&self, subject_id: &str, scope: &str, expires_at_utc: DateTime<Utc>) -> String {
        format!(
            "{}|{}|{}|{}",
            self.issuer,
            subject_id,
            scope,
            expires_at_utc.to_rfc3339_opts(chrono::SecondsFormat::AutoSi, true)
        )
    }
}

impl ICryptoDelegation for HmacCryptoDelegation {
    fn issue(&self, subject_id: &str, scope: &str, lifetime: Duration) -> DelegationCredential {
        assert!(!subject_id.trim().is_empty(), "subjectId required");
        assert!(!scope.trim().is_empty(), "scope required");
        assert!(lifetime > Duration::zero(), "lifetime out of range");
        let expires = Utc::now() + lifetime;
        let payload = self.canonical(subject_id, scope, expires);
        let sig = hmac_sha256(&self.key, payload.as_bytes());
        DelegationCredential::new(&self.issuer, subject_id, scope, expires, base64_encode(&sig))
    }

    fn verify(&self, credential: &DelegationCredential) -> bool {
        if credential.issuer != self.issuer {
            return false;
        }
        if credential.expires_at_utc <= Utc::now() {
            return false;
        }
        if credential.signature.is_empty() {
            return false;
        }
        let Some(sig) = base64_decode(&credential.signature) else {
            return false;
        };
        let payload = self.canonical(
            &credential.subject_id,
            &credential.scope,
            credential.expires_at_utc,
        );
        let expected = hmac_sha256(&self.key, payload.as_bytes());
        constant_time_eq(&sig, &expected)
    }
}

/// HMAC-SHA256 (FIPS 198-1) over the crate's vetted SHA-256 core.
fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    use crate::memory::multimodal::sha256;
    const BLOCK: usize = 64;
    let mut k = if key.len() > BLOCK {
        sha256(key).to_vec()
    } else {
        key.to_vec()
    };
    k.resize(BLOCK, 0);
    let mut ipad = [0x36u8; BLOCK];
    let mut opad = [0x5cu8; BLOCK];
    for i in 0..BLOCK {
        ipad[i] ^= k[i];
        opad[i] ^= k[i];
    }
    let mut inner = ipad.to_vec();
    inner.extend_from_slice(message);
    let inner_hash = sha256(&inner);
    let mut outer = opad.to_vec();
    outer.extend_from_slice(&inner_hash);
    sha256(&outer)
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for i in 0..a.len() {
        diff |= a[i] ^ b[i];
    }
    diff == 0
}

const B64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

fn base64_encode(data: &[u8]) -> String {
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(B64[((n >> 18) & 63) as usize] as char);
        out.push(B64[((n >> 12) & 63) as usize] as char);
        out.push(if chunk.len() > 1 {
            B64[((n >> 6) & 63) as usize] as char
        } else {
            '='
        });
        out.push(if chunk.len() > 2 {
            B64[(n & 63) as usize] as char
        } else {
            '='
        });
    }
    out
}

fn base64_decode(s: &str) -> Option<Vec<u8>> {
    fn val(c: u8) -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a' + 26) as u32),
            b'0'..=b'9' => Some((c - b'0' + 52) as u32),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }
    let bytes: Vec<u8> = s.bytes().filter(|&b| b != b'=' && !b.is_ascii_whitespace()).collect();
    let mut out = Vec::with_capacity(bytes.len() / 4 * 3);
    for chunk in bytes.chunks(4) {
        let mut n = 0u32;
        let mut bits = 0;
        for &c in chunk {
            n = (n << 6) | val(c)?;
            bits += 6;
        }
        // Align to the top and emit whole bytes.
        n <<= 24 - bits;
        let out_bytes = bits / 8;
        for i in 0..out_bytes {
            out.push(((n >> (16 - i * 8)) & 0xFF) as u8);
        }
    }
    Some(out)
}

// =====================================================================
// 23. CodeGenerationLoop — syntax-validate + run injected tests.
// =====================================================================

/// The outcome of a code-generation job.
#[derive(Debug, Clone, PartialEq)]
pub struct CodeGenJob {
    pub id: String,
    pub prompt: String,
    pub output_snippet: String,
    pub tests_pass: bool,
    pub deploy_hint: Option<String>,
}

impl CodeGenJob {
    pub fn new(
        id: impl Into<String>,
        prompt: impl Into<String>,
        output_snippet: impl Into<String>,
        tests_pass: bool,
        deploy_hint: Option<String>,
    ) -> Self {
        Self {
            id: id.into(),
            prompt: prompt.into(),
            output_snippet: output_snippet.into(),
            tests_pass,
            deploy_hint,
        }
    }
}

/// Generates a code snippet from a prompt (injected; a host wires an LLM).
pub type CodeGeneratorFn = Arc<dyn Fn(&str) -> String + Send + Sync>;
/// Runs the generated snippet's tests (injected).
pub type TestRunnerFn = Arc<dyn Fn(&str) -> bool + Send + Sync>;
/// Suggests a deployment hint for the snippet (injected).
pub type DeploymentHintFn = Arc<dyn Fn(&str) -> Option<String> + Send + Sync>;

/// Live code generation + test + deploy loop.
pub trait ICodeGenerationLoop: Send + Sync {
    /// Generates, syntax-checks, tests, and hints deployment for `prompt`.
    ///
    /// # Panics
    /// Panics if `prompt` is blank.
    fn run(&self, prompt: &str) -> CodeGenJob;
}

/// Syntax-checking code-generation loop. Generation, testing, and deploy-hinting
/// are injected closures (default generator echoes the prompt; default test
/// runner passes when brackets balance; default hint stages a nuget when the
/// snippet declares a class). 1:1 with the C# `SyntaxCheckingCodeGenerationLoop`.
pub struct SyntaxCheckingCodeGenerationLoop {
    generator: CodeGeneratorFn,
    test_runner: TestRunnerFn,
    deployment_hint: DeploymentHintFn,
}

impl Default for SyntaxCheckingCodeGenerationLoop {
    fn default() -> Self {
        Self::new(None, None, None)
    }
}

impl SyntaxCheckingCodeGenerationLoop {
    /// Creates a loop, defaulting any un-supplied closure to the reference default.
    pub fn new(
        generator: Option<CodeGeneratorFn>,
        test_runner: Option<TestRunnerFn>,
        deployment_hint: Option<DeploymentHintFn>,
    ) -> Self {
        let generator = generator.unwrap_or_else(|| {
            Arc::new(|prompt: &str| {
                format!(
                    "// generated from: {}\nreturn 0;",
                    prompt.replace('\n', " ")
                )
            })
        });
        let test_runner =
            test_runner.unwrap_or_else(|| Arc::new(|s: &str| is_syntactically_balanced(s)));
        let deployment_hint = deployment_hint.unwrap_or_else(|| {
            Arc::new(|s: &str| {
                Some(if s.contains("public class") {
                    "stage as nuget".to_string()
                } else {
                    "run inline".to_string()
                })
            })
        });
        Self {
            generator,
            test_runner,
            deployment_hint,
        }
    }
}

impl ICodeGenerationLoop for SyntaxCheckingCodeGenerationLoop {
    fn run(&self, prompt: &str) -> CodeGenJob {
        assert!(!prompt.trim().is_empty(), "prompt required");
        let id = Uuid::new_v4().simple().to_string();
        let snippet = (self.generator)(prompt);
        let parses = is_syntactically_balanced(&snippet);
        let tests_ok = parses && (self.test_runner)(&snippet);
        let hint = if tests_ok {
            (self.deployment_hint)(&snippet)
        } else {
            None
        };
        CodeGenJob::new(id, prompt, snippet, tests_ok, hint)
    }
}

/// Checks that `{}`, `()`, `[]` are balanced and never close before opening.
fn is_syntactically_balanced(snippet: &str) -> bool {
    if snippet.is_empty() {
        return false;
    }
    let (mut curly, mut paren, mut square) = (0i32, 0i32, 0i32);
    for c in snippet.chars() {
        match c {
            '{' => curly += 1,
            '}' => curly -= 1,
            '(' => paren += 1,
            ')' => paren -= 1,
            '[' => square += 1,
            ']' => square -= 1,
            _ => {}
        }
        if curly < 0 || paren < 0 || square < 0 {
            return false;
        }
    }
    curly == 0 && paren == 0 && square == 0
}

// =====================================================================
// 24. SelfImprovementLoop — track bench scores + apply improvements.
// =====================================================================

/// The verdict of one self-improvement cycle.
#[derive(Debug, Clone, PartialEq)]
pub struct SelfImprovementVerdict {
    pub improvements_applied: String,
    pub new_bench_score: f64,
}

impl SelfImprovementVerdict {
    pub fn new(improvements_applied: impl Into<String>, new_bench_score: f64) -> Self {
        Self {
            improvements_applied: improvements_applied.into(),
            new_bench_score,
        }
    }
}

/// Runs the named bench suite and returns its score (injected).
pub type RunBenchFn = Arc<dyn Fn(&str) -> f64 + Send + Sync>;
/// Proposes an improvement given the suite id and current score (injected).
pub type ProposeImprovementFn = Arc<dyn Fn(&str, f64) -> String + Send + Sync>;

/// Self-debugging / self-improvement loop.
pub trait ISelfImprovementLoop: Send + Sync {
    /// Runs one improvement cycle for `bench_suite_id`.
    ///
    /// # Panics
    /// Panics if `bench_suite_id` is blank.
    fn cycle(&self, bench_suite_id: &str) -> SelfImprovementVerdict;
}

/// Tracking self-improvement loop. Records the best score per suite; on no
/// regression it keeps the score, otherwise it asks the injected proposer for an
/// improvement. Bench running + proposal are injected closures. 1:1 with the C#
/// `TrackingSelfImprovementLoop`.
pub struct TrackingSelfImprovementLoop {
    best_scores: Mutex<HashMap<String, f64>>,
    run_bench: RunBenchFn,
    propose_improvement: ProposeImprovementFn,
}

impl Default for TrackingSelfImprovementLoop {
    fn default() -> Self {
        Self::new(None, None)
    }
}

impl TrackingSelfImprovementLoop {
    /// Creates a loop, defaulting any un-supplied closure to the reference default.
    pub fn new(run_bench: Option<RunBenchFn>, propose_improvement: Option<ProposeImprovementFn>) -> Self {
        let run_bench = run_bench.unwrap_or_else(|| {
            Arc::new(|id: &str| 0.5 + (stable_hash16(id) as f64) / 65535.0 * 0.5)
        });
        let propose_improvement = propose_improvement.unwrap_or_else(|| {
            Arc::new(|_id: &str, current: f64| {
                format!("retry-with-temperature-0 (score was {current:.3})")
            })
        });
        Self {
            best_scores: Mutex::new(HashMap::new()),
            run_bench,
            propose_improvement,
        }
    }

    /// The best score recorded for `bench_suite_id` (0 if none).
    pub fn best_score_for(&self, bench_suite_id: &str) -> f64 {
        self.best_scores
            .lock()
            .unwrap()
            .get(bench_suite_id)
            .copied()
            .unwrap_or(0.0)
    }
}

impl ISelfImprovementLoop for TrackingSelfImprovementLoop {
    fn cycle(&self, bench_suite_id: &str) -> SelfImprovementVerdict {
        assert!(!bench_suite_id.trim().is_empty(), "benchSuiteId required");
        let baseline = self.best_score_for(bench_suite_id);
        let current = (self.run_bench)(bench_suite_id);
        let applied = if current >= baseline {
            self.best_scores
                .lock()
                .unwrap()
                .insert(bench_suite_id.to_string(), current);
            if current > baseline {
                "new best".to_string()
            } else {
                "no regression".to_string()
            }
        } else {
            (self.propose_improvement)(bench_suite_id, current)
        };
        SelfImprovementVerdict::new(applied, current)
    }
}

/// A stable 16-bit content hash for the default bench score (replaces the C#
/// `string.GetHashCode() & 0xFFFF`, which is process-randomised and can't be
/// reproduced — this FNV-1a low-16 is deterministic across runs).
fn stable_hash16(s: &str) -> u16 {
    let mut hash: u32 = 0x811c_9dc5;
    for b in s.bytes() {
        hash ^= b as u32;
        hash = hash.wrapping_mul(0x0100_0193);
    }
    (hash & 0xFFFF) as u16
}
