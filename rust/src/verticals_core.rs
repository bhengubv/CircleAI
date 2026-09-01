//! Ten verticals: video, the build farm, autonomous business, code
//! understanding, observability, the observer, spec-driven development,
//! visualization, research, and micro-agents.
//!
//! EACH ONE HAS THE SAME THREE PARTS - a contract, an in-memory implementation
//! that really works, and a null one that refuses in its own words. The null is
//! not filler. It is what runs on a device that cannot do the thing, and the
//! sentence it returns is what a person actually reads, so each is written for
//! its own situation rather than shared.
//!
//! THE IN-MEMORY ONES ARE REAL. An in-memory index that returns nothing teaches
//! nobody anything and hides the bugs that only appear once data flows.

use std::collections::{HashMap, HashSet};

// ─────────────────────────────────────────────────────────────────────────────
// Video

/// Frame size.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VideoResolution {
    pub width: u32,
    pub height: u32,
}

impl Default for VideoResolution {
    /// Portrait 720x1280. The phone is the target and the phone is held
    /// upright - a landscape default produces video nobody watches full-screen.
    fn default() -> Self {
        Self { width: 720, height: 1280 }
    }
}

impl VideoResolution {
    pub fn is_portrait(&self) -> bool {
        self.height > self.width
    }

    pub fn aspect(&self) -> f32 {
        if self.height == 0 {
            0.0
        } else {
            self.width as f32 / self.height as f32
        }
    }

    /// Both dimensions EVEN. Most encoders reject odd dimensions outright, and
    /// the ones that do not pad silently and shift the whole frame half a pixel.
    pub fn is_encodable(&self) -> bool {
        self.width >= 16 && self.height >= 16 && self.width % 2 == 0 && self.height % 2 == 0
    }
}

/// Names a style.
///
/// A newtype rather than a string, because a style id and a video id are both
/// strings and passing one where the other belongs is otherwise a runtime
/// surprise.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct StyleId(pub String);

impl StyleId {
    pub fn new(value: &str) -> Option<Self> {
        let trimmed = value.trim();
        (!trimmed.is_empty()).then(|| Self(trimmed.to_lowercase()))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// Where a style came from and what may be done with it.
///
/// THE WHOLE REASON A STYLE IS NOT JUST AN IMAGE. A style learnt from somebody's
/// work carries their claim with it, and a system that drops the attribution at
/// the first hop is a system that launders it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StyleAttribution {
    pub source: String,
    pub author: String,
    pub licence: String,
    /// Whether the person who made the source agreed to this use. `None` means
    /// UNKNOWN, which is not the same as no - and is treated as no.
    pub consented: Option<bool>,
    pub note: String,
}

impl StyleAttribution {
    /// Consent must be PRESENT and true. Absent is refused, because the default
    /// answer to "may I train on your work" is not yes.
    pub fn is_usable(&self) -> bool {
        self.consented == Some(true) && !self.source.is_empty()
    }

    pub fn describe(&self) -> String {
        match self.consented {
            Some(true) => format!(
                "{} by {} ({})",
                self.source,
                if self.author.is_empty() { "unknown" } else { &self.author },
                if self.licence.is_empty() { "licence unstated" } else { &self.licence }
            ),
            Some(false) => format!("{} - the author said no", self.source),
            None => format!("{} - nobody asked the author", self.source),
        }
    }
}

/// One frame that carries the look.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StyleReferenceFrame {
    pub frame_index: usize,
    pub image_path: String,
    pub caption: String,
}

/// A style, its frames, and its provenance.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StyleReference {
    pub id: StyleId,
    pub display_name: String,
    pub frames: Vec<StyleReferenceFrame>,
    pub attribution: StyleAttribution,
}

impl StyleReference {
    /// Frames alone are NOT enough. A style with images and no consent is
    /// exactly the case this refuses.
    pub fn is_usable(&self) -> bool {
        !self.frames.is_empty() && self.attribution.is_usable()
    }
}

/// Holds styles.
pub trait StyleReferenceStore {
    fn get(&self, id: &StyleId) -> Option<StyleReference>;
    fn put(&mut self, style: StyleReference) -> Result<(), String>;
    fn list(&self) -> Vec<StyleReference>;
}

/// Styles held in memory.
#[derive(Debug, Default)]
pub struct InMemoryStyleReference {
    styles: HashMap<StyleId, StyleReference>,
}

impl InMemoryStyleReference {
    pub fn new() -> Self {
        Self::default()
    }
}

impl StyleReferenceStore for InMemoryStyleReference {
    fn get(&self, id: &StyleId) -> Option<StyleReference> {
        self.styles.get(id).cloned()
    }

    /// REFUSES AT THE DOOR. A style without consent cannot be stored, so no
    /// later code has to remember to check - the unusable state never exists.
    fn put(&mut self, style: StyleReference) -> Result<(), String> {
        if style.id.as_str().is_empty() {
            return Err("a style needs a name".into());
        }
        if !style.attribution.is_usable() {
            return Err(format!(
                "that style will not be stored: {}",
                style.attribution.describe()
            ));
        }
        self.styles.insert(style.id.clone(), style);
        Ok(())
    }

    fn list(&self) -> Vec<StyleReference> {
        let mut out: Vec<StyleReference> = self.styles.values().cloned().collect();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }
}

/// Asks for a script in a style.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StyleScriptRequest {
    pub style: StyleId,
    pub brief: String,
    pub target_seconds: u32,
    pub language: String,
}

/// A script, as shots.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StyleScriptResult {
    /// `(seconds, description, spoken line)`.
    pub shots: Vec<(f32, String, String)>,
    pub total_seconds: f32,
    pub attribution: StyleAttribution,
    pub error: String,
}

impl StyleScriptResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_empty() && !self.shots.is_empty()
    }
}

/// Writes a script in a style.
pub trait StyleScript {
    fn is_available(&self) -> bool;
    fn write(&self, request: &StyleScriptRequest) -> StyleScriptResult;
}

/// Writes nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullStyleScript;

impl StyleScript for NullStyleScript {
    fn is_available(&self) -> bool {
        false
    }
    fn write(&self, _request: &StyleScriptRequest) -> StyleScriptResult {
        StyleScriptResult {
            error: "this device has no model that can write a script".into(),
            ..Default::default()
        }
    }
}

/// Asks for a video.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct VideoGenerationRequest {
    pub prompt: String,
    pub style: Option<StyleId>,
    pub resolution: VideoResolution,
    pub duration_seconds: u32,
    pub fps: u32,
    pub seed: u32,
}

/// The audio that goes with it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AudioTrack {
    pub path: String,
    pub sample_rate_hz: u32,
    pub channels: u16,
    /// What made it, so a clip using a generated bed can say so. Silence about
    /// provenance is how attribution gets lost one hop at a time.
    pub origin: String,
}

/// What came back.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct VideoGenerationResult {
    pub path: String,
    pub resolution: VideoResolution,
    pub duration_seconds: f32,
    pub audio: Option<AudioTrack>,
    /// Carried THROUGH to the output. A video made in somebody's style says so
    /// on the way out, not only on the way in.
    pub attribution: StyleAttribution,
    pub error: String,
}

impl VideoGenerationResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_empty() && !self.path.is_empty()
    }
}

/// Makes video.
pub trait VideoGenerator {
    fn is_available(&self) -> bool;
    fn generate(&self, request: &VideoGenerationRequest) -> VideoGenerationResult;
}

/// Makes none.
///
/// Video generation needs a model this device does not have, and there is no
/// procedural fallback worth shipping - unlike a music bed, which sine tones can
/// honestly make.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVideoGenerator;

impl VideoGenerator for NullVideoGenerator {
    fn is_available(&self) -> bool {
        false
    }
    fn generate(&self, _request: &VideoGenerationRequest) -> VideoGenerationResult {
        VideoGenerationResult {
            error: "this device cannot generate video; nothing was sent anywhere".into(),
            ..Default::default()
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Build farm

/// What kind of machine a build runs on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum BuildAgentKind {
    #[default]
    Local,
    /// A machine on the network. THE ONE THAT NEEDS A DIFFERENT SECRET STORY -
    /// a signing key that reaches a remote agent has left this device.
    Remote,
    /// A phone. Slow, and the only place an on-device claim can be earned.
    Device,
    Container,
}

/// A machine that can build.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BuildAgent {
    pub agent_id: String,
    pub kind: BuildAgentKind,
    pub platform: String,
    pub concurrency: usize,
    pub labels: HashSet<String>,
    pub online: bool,
}

impl BuildAgent {
    /// Labels are how a job that needs a Mac finds one. Matching is by SUBSET -
    /// an agent with more labels than asked for still qualifies.
    pub fn satisfies(&self, required: &HashSet<String>) -> bool {
        self.online && required.is_subset(&self.labels)
    }
}

/// Where a job has got to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum BuildJobPhase {
    #[default]
    Queued,
    /// Waiting for a machine that can take it. Distinct from Queued, because a
    /// job that has waited an hour for a Mac is a capacity problem and a job
    /// that has waited an hour in the queue is a throughput one.
    AwaitingAgent,
    Fetching,
    Building,
    Testing,
    Packaging,
    Succeeded,
    Failed,
    Cancelled,
}

impl BuildJobPhase {
    pub fn is_terminal(&self) -> bool {
        matches!(self, Self::Succeeded | Self::Failed | Self::Cancelled)
    }
}

/// One build.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BuildJob {
    pub job_id: String,
    pub project: String,
    pub revision: String,
    pub required_labels: HashSet<String>,
    pub phase: BuildJobPhase,
    pub agent_id: String,
    pub log_tail: Vec<String>,
    pub queued_at_ms: u64,
    pub finished_at_ms: u64,
}

impl BuildJob {
    /// How long it has been waiting or ran. Zero rather than a wrap when the
    /// clock is not what was expected.
    pub fn elapsed_ms(&self, now_ms: u64) -> u64 {
        let end = if self.finished_at_ms > 0 { self.finished_at_ms } else { now_ms };
        end.saturating_sub(self.queued_at_ms)
    }
}

/// Something a build produced.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BuildArtifact {
    pub artifact_id: String,
    pub job_id: String,
    pub file_name: String,
    pub bytes: u64,
    /// An artifact WITHOUT a digest is not storable. An unverifiable binary
    /// leaving a build farm is how a supply chain gets its first bad link.
    pub sha256: String,
}

/// Hands out machines.
pub trait BuildAgentPool {
    fn register(&mut self, agent: BuildAgent);
    /// `None` when nothing matches. NOT the least-bad machine: a build that
    /// needs a Mac and gets a Linux box fails confusingly ten minutes later.
    fn claim(&mut self, required: &HashSet<String>) -> Option<BuildAgent>;
    fn release(&mut self, agent_id: &str);
    fn agents(&self) -> Vec<BuildAgent>;
}

/// A pool in memory.
#[derive(Debug, Default)]
pub struct InMemoryBuildAgentPool {
    agents: Vec<BuildAgent>,
    in_use: HashMap<String, usize>,
}

impl InMemoryBuildAgentPool {
    pub fn new() -> Self {
        Self::default()
    }

    fn free_slots(&self, agent: &BuildAgent) -> usize {
        agent
            .concurrency
            .max(1)
            .saturating_sub(*self.in_use.get(&agent.agent_id).unwrap_or(&0))
    }
}

impl BuildAgentPool for InMemoryBuildAgentPool {
    fn register(&mut self, agent: BuildAgent) {
        if let Some(existing) = self.agents.iter_mut().find(|a| a.agent_id == agent.agent_id) {
            *existing = agent;
        } else {
            self.agents.push(agent);
        }
    }

    fn claim(&mut self, required: &HashSet<String>) -> Option<BuildAgent> {
        // The EMPTIEST matching agent, so work spreads rather than piling onto
        // whichever machine registered first.
        let chosen = self
            .agents
            .iter()
            .filter(|a| a.satisfies(required) && self.free_slots(a) > 0)
            .max_by_key(|a| self.free_slots(a))
            .cloned()?;
        *self.in_use.entry(chosen.agent_id.clone()).or_insert(0) += 1;
        Some(chosen)
    }

    fn release(&mut self, agent_id: &str) {
        if let Some(count) = self.in_use.get_mut(agent_id) {
            *count = count.saturating_sub(1);
        }
    }

    fn agents(&self) -> Vec<BuildAgent> {
        self.agents.clone()
    }
}

/// A pool with no machines.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullBuildAgentPool;

impl BuildAgentPool for NullBuildAgentPool {
    fn register(&mut self, _agent: BuildAgent) {}
    fn claim(&mut self, _required: &HashSet<String>) -> Option<BuildAgent> {
        None
    }
    fn release(&mut self, _agent_id: &str) {}
    fn agents(&self) -> Vec<BuildAgent> {
        Vec::new()
    }
}

/// Keeps what builds produced.
pub trait BuildArtifactStore {
    fn put(&mut self, artifact: BuildArtifact, bytes: Vec<u8>) -> Result<(), String>;
    fn get(&self, artifact_id: &str) -> Option<(BuildArtifact, Vec<u8>)>;
    fn for_job(&self, job_id: &str) -> Vec<BuildArtifact>;
}

/// Artifacts in memory.
#[derive(Debug, Default)]
pub struct InMemoryBuildArtifactStore {
    artifacts: HashMap<String, (BuildArtifact, Vec<u8>)>,
}

impl InMemoryBuildArtifactStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl BuildArtifactStore for InMemoryBuildArtifactStore {
    fn put(&mut self, artifact: BuildArtifact, bytes: Vec<u8>) -> Result<(), String> {
        if artifact.sha256.trim().is_empty() {
            return Err("an artifact without a checksum will not be stored".into());
        }
        // The RECORDED size must match what arrived. A mismatch means one of the
        // two is wrong, and finding out later means finding out from a truncated
        // download.
        if artifact.bytes != 0 && artifact.bytes != bytes.len() as u64 {
            return Err(format!(
                "that artifact says {} bytes but {} arrived",
                artifact.bytes,
                bytes.len()
            ));
        }
        self.artifacts
            .insert(artifact.artifact_id.clone(), (artifact, bytes));
        Ok(())
    }

    fn get(&self, artifact_id: &str) -> Option<(BuildArtifact, Vec<u8>)> {
        self.artifacts.get(artifact_id).cloned()
    }

    fn for_job(&self, job_id: &str) -> Vec<BuildArtifact> {
        let mut out: Vec<BuildArtifact> = self
            .artifacts
            .values()
            .filter(|(a, _)| a.job_id == job_id)
            .map(|(a, _)| a.clone())
            .collect();
        out.sort_by(|a, b| a.file_name.cmp(&b.file_name));
        out
    }
}

/// Keeps nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullBuildArtifactStore;

impl BuildArtifactStore for NullBuildArtifactStore {
    fn put(&mut self, _artifact: BuildArtifact, _bytes: Vec<u8>) -> Result<(), String> {
        Err("no artifact store is configured, so the build output was discarded".into())
    }
    fn get(&self, _artifact_id: &str) -> Option<(BuildArtifact, Vec<u8>)> {
        None
    }
    fn for_job(&self, _job_id: &str) -> Vec<BuildArtifact> {
        Vec::new()
    }
}

/// Runs builds.
pub trait BuildJobRunner {
    fn submit(&mut self, job: BuildJob) -> Result<String, String>;
    fn advance(&mut self, job_id: &str, phase: BuildJobPhase, now_ms: u64) -> bool;
    fn job(&self, job_id: &str) -> Option<BuildJob>;
    fn queue(&self) -> Vec<BuildJob>;
}

/// A runner in memory.
///
/// The phase machine is real: a job cannot go backwards and cannot leave a
/// terminal phase. Without that, a late message from a machine that has already
/// finished resurrects a completed job.
#[derive(Debug, Default)]
pub struct InMemoryBuildJobRunner {
    jobs: HashMap<String, BuildJob>,
    order: Vec<String>,
}

impl InMemoryBuildJobRunner {
    pub fn new() -> Self {
        Self::default()
    }

    fn rank(phase: BuildJobPhase) -> u8 {
        match phase {
            BuildJobPhase::Queued => 0,
            BuildJobPhase::AwaitingAgent => 1,
            BuildJobPhase::Fetching => 2,
            BuildJobPhase::Building => 3,
            BuildJobPhase::Testing => 4,
            BuildJobPhase::Packaging => 5,
            _ => 6,
        }
    }
}

impl BuildJobRunner for InMemoryBuildJobRunner {
    fn submit(&mut self, job: BuildJob) -> Result<String, String> {
        if job.job_id.trim().is_empty() {
            return Err("a job needs an identifier".into());
        }
        if self.jobs.contains_key(&job.job_id) {
            return Err(format!("job {} was already submitted", job.job_id));
        }
        let id = job.job_id.clone();
        self.order.push(id.clone());
        self.jobs.insert(id.clone(), job);
        Ok(id)
    }

    fn advance(&mut self, job_id: &str, phase: BuildJobPhase, now_ms: u64) -> bool {
        let Some(job) = self.jobs.get_mut(job_id) else { return false };
        if job.phase.is_terminal() || Self::rank(phase) < Self::rank(job.phase) {
            return false;
        }
        job.phase = phase;
        if phase.is_terminal() {
            job.finished_at_ms = now_ms;
        }
        true
    }

    fn job(&self, job_id: &str) -> Option<BuildJob> {
        self.jobs.get(job_id).cloned()
    }

    fn queue(&self) -> Vec<BuildJob> {
        self.order
            .iter()
            .filter_map(|id| self.jobs.get(id))
            .filter(|j| !j.phase.is_terminal())
            .cloned()
            .collect()
    }
}

/// Runs nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullBuildJobRunner;

impl BuildJobRunner for NullBuildJobRunner {
    fn submit(&mut self, _job: BuildJob) -> Result<String, String> {
        Err("no build farm is configured on this device".into())
    }
    fn advance(&mut self, _job_id: &str, _phase: BuildJobPhase, _now_ms: u64) -> bool {
        false
    }
    fn job(&self, _job_id: &str) -> Option<BuildJob> {
        None
    }
    fn queue(&self) -> Vec<BuildJob> {
        Vec::new()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Autonomous business

/// Money arriving or leaving, in MINOR UNITS.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RevenueEvent {
    pub event_id: String,
    pub source: String,
    pub amount_minor: i64,
    pub currency: String,
    pub at_ms: u64,
    /// What it was for, in the words a person would use on a statement.
    pub description: String,
}

impl RevenueEvent {
    pub fn is_inflow(&self) -> bool {
        self.amount_minor > 0
    }
}

/// What is held, per currency.
///
/// PER CURRENCY, never summed. Adding rand to dollars needs a rate, a rate needs
/// a time, and a treasury that silently picks either produces a number nobody
/// can reconcile.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TreasurySnapshot {
    pub balances_minor: HashMap<String, i64>,
    pub as_of_ms: u64,
}

impl TreasurySnapshot {
    pub fn balance(&self, currency: &str) -> i64 {
        *self.balances_minor.get(currency).unwrap_or(&0)
    }

    /// Any currency in the red. Returned as a list rather than a bool, because
    /// which one is overdrawn is the actionable part.
    pub fn overdrawn(&self) -> Vec<String> {
        let mut out: Vec<String> = self
            .balances_minor
            .iter()
            .filter(|(_, v)| **v < 0)
            .map(|(k, _)| k.clone())
            .collect();
        out.sort();
        out
    }
}

/// Something the system decided by itself.
///
/// EVERY FIELD IS FOR THE REVIEW AFTERWARDS. A decision log that records what
/// happened but not why, or why but not what was rejected, cannot answer the
/// only question anybody asks it: how did it come to that.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AutonomousDecision {
    pub decision_id: String,
    pub action: String,
    pub rationale: String,
    /// What was considered and NOT chosen. The most useful field, and the one
    /// most often dropped.
    pub alternatives: Vec<String>,
    pub confidence: f32,
    pub at_ms: u64,
    /// Whether a person approved it. `None` means it has not been reviewed - not
    /// that it was approved.
    pub human_approved: Option<bool>,
    pub reversible: bool,
}

impl AutonomousDecision {
    /// Needs a person if it CANNOT be undone or the system is unsure.
    ///
    /// The two conditions are separate on purpose: a confident irreversible
    /// decision still needs a person, because confidence is not the same as
    /// being right and an irreversible mistake cannot be walked back.
    pub fn needs_human(&self) -> bool {
        !self.reversible || self.confidence < 0.8
    }
}

/// Records decisions.
pub trait DecisionLog {
    fn record(&mut self, decision: AutonomousDecision) -> Result<(), String>;
    fn recent(&self, limit: usize) -> Vec<AutonomousDecision>;
    fn awaiting_review(&self) -> Vec<AutonomousDecision>;
}

/// A log in memory.
#[derive(Debug, Default)]
pub struct InMemoryDecisionLog {
    decisions: Vec<AutonomousDecision>,
}

impl InMemoryDecisionLog {
    pub fn new() -> Self {
        Self::default()
    }
}

impl DecisionLog for InMemoryDecisionLog {
    /// A DECISION WITHOUT A RATIONALE IS REFUSED. The log exists to answer "why",
    /// and an entry that cannot is worse than none - it makes the record look
    /// complete.
    fn record(&mut self, decision: AutonomousDecision) -> Result<(), String> {
        if decision.rationale.trim().is_empty() {
            return Err("a decision without a reason will not be logged".into());
        }
        self.decisions.push(decision);
        Ok(())
    }

    fn recent(&self, limit: usize) -> Vec<AutonomousDecision> {
        let mut out = self.decisions.clone();
        out.sort_by(|a, b| b.at_ms.cmp(&a.at_ms));
        out.truncate(if limit == 0 { 20 } else { limit });
        out
    }

    fn awaiting_review(&self) -> Vec<AutonomousDecision> {
        self.decisions
            .iter()
            .filter(|d| d.needs_human() && d.human_approved.is_none())
            .cloned()
            .collect()
    }
}

/// Records nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDecisionLog;

impl DecisionLog for NullDecisionLog {
    fn record(&mut self, _decision: AutonomousDecision) -> Result<(), String> {
        Err("no decision log is configured, so nothing autonomous should run".into())
    }
    fn recent(&self, _limit: usize) -> Vec<AutonomousDecision> {
        Vec::new()
    }
    fn awaiting_review(&self) -> Vec<AutonomousDecision> {
        Vec::new()
    }
}

/// Holds money.
pub trait Treasury {
    fn apply(&mut self, event: &RevenueEvent) -> Result<(), String>;
    fn snapshot(&self, now_ms: u64) -> TreasurySnapshot;
    /// Whether an outflow may proceed. NEVER moves anything - checking and
    /// paying are separate calls so a check cannot become a payment by
    /// accident.
    fn can_spend(&self, amount_minor: i64, currency: &str) -> bool;
}

/// A treasury in memory.
#[derive(Debug, Default)]
pub struct InMemoryTreasury {
    balances: HashMap<String, i64>,
    seen: HashSet<String>,
}

impl InMemoryTreasury {
    pub fn new() -> Self {
        Self::default()
    }
}

impl Treasury for InMemoryTreasury {
    /// IDEMPOTENT BY EVENT ID. A payment notification arriving twice is normal,
    /// and a treasury that counts it twice is wrong in the direction that gets
    /// noticed by an auditor rather than a user.
    fn apply(&mut self, event: &RevenueEvent) -> Result<(), String> {
        if event.event_id.trim().is_empty() {
            return Err("a revenue event needs an identifier to be idempotent".into());
        }
        if event.currency.trim().is_empty() {
            return Err("a revenue event needs a currency".into());
        }
        if !self.seen.insert(event.event_id.clone()) {
            return Ok(());
        }
        *self.balances.entry(event.currency.clone()).or_insert(0) += event.amount_minor;
        Ok(())
    }

    fn snapshot(&self, now_ms: u64) -> TreasurySnapshot {
        TreasurySnapshot { balances_minor: self.balances.clone(), as_of_ms: now_ms }
    }

    fn can_spend(&self, amount_minor: i64, currency: &str) -> bool {
        amount_minor > 0 && *self.balances.get(currency).unwrap_or(&0) >= amount_minor
    }
}

/// Holds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullTreasury;

impl Treasury for NullTreasury {
    fn apply(&mut self, _event: &RevenueEvent) -> Result<(), String> {
        Err("no treasury is configured; money was not recorded".into())
    }
    fn snapshot(&self, now_ms: u64) -> TreasurySnapshot {
        TreasurySnapshot { balances_minor: HashMap::new(), as_of_ms: now_ms }
    }
    /// ALWAYS FALSE. An unconfigured treasury that permitted spending would
    /// approve every payment on a device that cannot track any of them.
    fn can_spend(&self, _amount_minor: i64, _currency: &str) -> bool {
        false
    }
}

/// The loop that earns.
pub trait RevenueLoop {
    fn is_running(&self) -> bool;
    fn tick(&mut self, now_ms: u64) -> Vec<AutonomousDecision>;
    fn stop(&mut self);
}

/// A loop in memory.
#[derive(Default)]
pub struct InMemoryRevenueLoop {
    running: bool,
    #[allow(clippy::type_complexity)]
    propose: Option<Box<dyn Fn(u64) -> Vec<AutonomousDecision> + Send + Sync>>,
    /// A ceiling on decisions per tick. An autonomous loop that can act without
    /// limit is one bad prompt away from acting a thousand times.
    max_per_tick: usize,
}

impl InMemoryRevenueLoop {
    #[allow(clippy::type_complexity)]
    pub fn new(
        propose: Option<Box<dyn Fn(u64) -> Vec<AutonomousDecision> + Send + Sync>>,
        max_per_tick: usize,
    ) -> Self {
        Self {
            running: true,
            propose,
            max_per_tick: if max_per_tick == 0 { 5 } else { max_per_tick },
        }
    }
}

impl RevenueLoop for InMemoryRevenueLoop {
    fn is_running(&self) -> bool {
        self.running
    }

    fn tick(&mut self, now_ms: u64) -> Vec<AutonomousDecision> {
        if !self.running {
            return Vec::new();
        }
        let Some(propose) = &self.propose else { return Vec::new() };
        let mut decisions = propose(now_ms);
        decisions.truncate(self.max_per_tick);
        decisions
    }

    fn stop(&mut self) {
        self.running = false;
    }
}

/// A loop that does not run.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullRevenueLoop;

impl RevenueLoop for NullRevenueLoop {
    fn is_running(&self) -> bool {
        false
    }
    fn tick(&mut self, _now_ms: u64) -> Vec<AutonomousDecision> {
        Vec::new()
    }
    fn stop(&mut self) {}
}

// ─────────────────────────────────────────────────────────────────────────────
// Code understanding

/// Something named in code.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct CodeSymbol {
    pub name: String,
    pub kind: String,
    pub file: String,
    pub line: usize,
    /// The containing type or module. What makes two same-named methods
    /// distinguishable, which they very often are.
    pub container: String,
    pub language: String,
}

impl CodeSymbol {
    /// Unique enough to key on. File AND line, because a name is not unique and
    /// a file is not either.
    pub fn key(&self) -> String {
        format!("{}:{}:{}", self.file, self.line, self.name)
    }

    pub fn qualified(&self) -> String {
        if self.container.is_empty() {
            self.name.clone()
        } else {
            format!("{}.{}", self.container, self.name)
        }
    }
}

/// One hit.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CodeMatch {
    pub symbol: CodeSymbol,
    pub score: f32,
    /// The line itself, so a result list is readable without opening anything.
    pub excerpt: String,
}

/// One symbol referring to another.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct SymbolEdge {
    pub from: String,
    pub to: String,
    /// "calls", "implements", "extends", "reads". The kind is what makes a graph
    /// answerable rather than merely connected.
    pub relation: String,
}

/// Builds an index.
pub trait CodeIndexer {
    fn index_file(&mut self, path: &str, content: &str, language: &str) -> usize;
    fn symbols(&self) -> Vec<CodeSymbol>;
    fn clear(&mut self);
}

/// Indexes by reading declarations.
///
/// LINE-BASED, and honest about it: it finds declarations, not semantics. A real
/// parser is a per-language dependency this port does not carry, and a
/// half-parser that claimed to be one would be worse than a scanner that says
/// what it is.
#[derive(Debug, Default)]
pub struct FilesystemCodeIndexer {
    symbols: Vec<CodeSymbol>,
}

impl FilesystemCodeIndexer {
    pub fn new() -> Self {
        Self::default()
    }

    /// The keyword that introduces a declaration, if the line has one.
    fn declaration_kind(line: &str, language: &str) -> Option<&'static str> {
        let trimmed = line.trim_start();
        let starts = |k: &str| {
            trimmed
                .strip_prefix(k)
                .map(|rest| rest.starts_with(char::is_whitespace))
                .unwrap_or(false)
        };
        let keywords: &[(&str, &str)] = match language {
            "rust" => &[
                ("struct", "struct"), ("enum", "enum"), ("trait", "trait"),
                ("fn", "function"), ("mod", "module"), ("type", "alias"),
                ("const", "constant"), ("static", "static"), ("union", "union"),
            ],
            "python" => &[("class", "class"), ("def", "function")],
            "typescript" | "javascript" => &[
                ("class", "class"), ("interface", "interface"), ("enum", "enum"),
                ("function", "function"), ("type", "alias"),
            ],
            _ => &[
                ("class", "class"), ("struct", "struct"), ("interface", "interface"),
                ("enum", "enum"), ("record", "record"),
            ],
        };
        // Visibility modifiers are stripped first, so `pub fn` and `public class`
        // are found. Without this the scanner sees only private declarations,
        // which is precisely backwards.
        for prefix in ["pub(crate) ", "pub ", "public ", "internal ", "private ", "protected ", "export ", "async "] {
            if let Some(rest) = trimmed.strip_prefix(prefix) {
                return Self::declaration_kind(rest, language);
            }
        }
        keywords.iter().find(|(k, _)| starts(k)).map(|(_, v)| *v)
    }

    fn name_after(line: &str, kind_word_count: usize) -> String {
        line.trim_start()
            .split_whitespace()
            .nth(kind_word_count)
            .unwrap_or("")
            .trim_matches(|c: char| !c.is_alphanumeric() && c != '_')
            .to_string()
    }
}

impl CodeIndexer for FilesystemCodeIndexer {
    fn index_file(&mut self, path: &str, content: &str, language: &str) -> usize {
        let mut added = 0;
        for (index, line) in content.lines().enumerate() {
            let Some(kind) = Self::declaration_kind(line, language) else { continue };
            let name = Self::name_after(line, 1);
            if name.is_empty() {
                continue;
            }
            self.symbols.push(CodeSymbol {
                name,
                kind: kind.to_string(),
                file: path.to_string(),
                line: index + 1,
                container: String::new(),
                language: language.to_string(),
            });
            added += 1;
        }
        added
    }

    fn symbols(&self) -> Vec<CodeSymbol> {
        self.symbols.clone()
    }

    fn clear(&mut self) {
        self.symbols.clear();
    }
}

/// Indexes nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCodeIndexer;

impl CodeIndexer for NullCodeIndexer {
    fn index_file(&mut self, _path: &str, _content: &str, _language: &str) -> usize {
        0
    }
    fn symbols(&self) -> Vec<CodeSymbol> {
        Vec::new()
    }
    fn clear(&mut self) {}
}

/// Searches code.
pub trait CodeSearch {
    fn search(&self, query: &str, limit: usize) -> Vec<CodeMatch>;
}

/// Searches what an indexer found.
pub struct IndexBackedCodeSearch {
    symbols: Vec<CodeSymbol>,
}

impl IndexBackedCodeSearch {
    pub fn new(symbols: Vec<CodeSymbol>) -> Self {
        Self { symbols }
    }

    /// Exact beats prefix beats contains.
    ///
    /// Somebody typing a full name wants that name FIRST, and a plain substring
    /// search buries it under every longer name containing it.
    fn score(name: &str, query: &str) -> f32 {
        let (name, query) = (name.to_lowercase(), query.to_lowercase());
        if name == query {
            1.0
        } else if name.starts_with(&query) {
            0.75
        } else if name.contains(&query) {
            0.5
        } else {
            0.0
        }
    }
}

impl CodeSearch for IndexBackedCodeSearch {
    fn search(&self, query: &str, limit: usize) -> Vec<CodeMatch> {
        if query.trim().is_empty() {
            return Vec::new();
        }
        let mut hits: Vec<CodeMatch> = self
            .symbols
            .iter()
            .filter_map(|s| {
                let score = Self::score(&s.name, query);
                (score > 0.0).then(|| CodeMatch {
                    excerpt: format!("{} {}", s.kind, s.qualified()),
                    symbol: s.clone(),
                    score,
                })
            })
            .collect();
        // Ties break by file and line so results are STABLE. An unstable order
        // makes a result list shuffle between identical searches, which reads as
        // a bug even when the set is right.
        hits.sort_by(|a, b| {
            b.score
                .partial_cmp(&a.score)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.symbol.file.cmp(&b.symbol.file))
                .then_with(|| a.symbol.line.cmp(&b.symbol.line))
        });
        hits.truncate(if limit == 0 { 20 } else { limit });
        hits
    }
}

/// Finds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCodeSearch;

impl CodeSearch for NullCodeSearch {
    fn search(&self, _query: &str, _limit: usize) -> Vec<CodeMatch> {
        Vec::new()
    }
}

/// What refers to what.
pub trait SymbolGraph {
    fn add_edge(&mut self, edge: SymbolEdge);
    fn outgoing(&self, symbol: &str) -> Vec<SymbolEdge>;
    fn incoming(&self, symbol: &str) -> Vec<SymbolEdge>;
    /// Everything reachable, up to a depth. BOUNDED, because a call graph has
    /// cycles and an unbounded walk on one does not return.
    fn reachable(&self, from: &str, max_depth: usize) -> Vec<String>;
}

/// A graph in memory.
#[derive(Debug, Default)]
pub struct InMemorySymbolGraph {
    edges: Vec<SymbolEdge>,
}

impl InMemorySymbolGraph {
    pub fn new() -> Self {
        Self::default()
    }
}

impl SymbolGraph for InMemorySymbolGraph {
    fn add_edge(&mut self, edge: SymbolEdge) {
        if edge.from.is_empty() || edge.to.is_empty() || self.edges.contains(&edge) {
            return;
        }
        self.edges.push(edge);
    }

    fn outgoing(&self, symbol: &str) -> Vec<SymbolEdge> {
        self.edges.iter().filter(|e| e.from == symbol).cloned().collect()
    }

    fn incoming(&self, symbol: &str) -> Vec<SymbolEdge> {
        self.edges.iter().filter(|e| e.to == symbol).cloned().collect()
    }

    fn reachable(&self, from: &str, max_depth: usize) -> Vec<String> {
        let mut seen: HashSet<String> = HashSet::from([from.to_string()]);
        let mut frontier = vec![from.to_string()];
        let mut out = Vec::new();
        for _ in 0..max_depth.max(1) {
            let mut next = Vec::new();
            for node in &frontier {
                for edge in self.outgoing(node) {
                    // The `seen` set is what makes a cycle terminate. Recursion
                    // without it on a mutually recursive pair never returns.
                    if seen.insert(edge.to.clone()) {
                        out.push(edge.to.clone());
                        next.push(edge.to);
                    }
                }
            }
            if next.is_empty() {
                break;
            }
            frontier = next;
        }
        out.sort();
        out
    }
}

/// A graph with no edges.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSymbolGraph;

impl SymbolGraph for NullSymbolGraph {
    fn add_edge(&mut self, _edge: SymbolEdge) {}
    fn outgoing(&self, _symbol: &str) -> Vec<SymbolEdge> {
        Vec::new()
    }
    fn incoming(&self, _symbol: &str) -> Vec<SymbolEdge> {
        Vec::new()
    }
    fn reachable(&self, _from: &str, _max_depth: usize) -> Vec<String> {
        Vec::new()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Observability

/// One measurement.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct MetricSample {
    pub name: String,
    pub value: f64,
    /// Dimensions. DELIBERATELY LOW-CARDINALITY - a label carrying a user or
    /// request id turns one metric into a million and is also a way of
    /// smuggling personal data into telemetry.
    pub labels: HashMap<String, String>,
    pub at_ms: u64,
}

impl MetricSample {
    /// A rough cardinality guard.
    ///
    /// Long label values are almost always identifiers, and an identifier in a
    /// metric label is the failure this checks for.
    pub fn looks_high_cardinality(&self) -> bool {
        self.labels.len() > 8 || self.labels.values().any(|v| v.len() > 64)
    }
}

/// One span of work.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TraceSpan {
    pub trace_id: String,
    pub span_id: String,
    /// Empty for a root. A span whose parent is missing is an ORPHAN, which is
    /// usually a dropped message rather than a root.
    pub parent_span_id: String,
    pub name: String,
    pub started_at_ms: u64,
    pub ended_at_ms: u64,
    pub attributes: HashMap<String, String>,
}

impl TraceSpan {
    pub fn duration_ms(&self) -> u64 {
        self.ended_at_ms.saturating_sub(self.started_at_ms)
    }

    /// Whether it finished. An unfinished span is not a zero-length one, and a
    /// trace view that renders it as such hides the exact case worth seeing.
    pub fn is_complete(&self) -> bool {
        self.ended_at_ms >= self.started_at_ms && self.ended_at_ms > 0
    }
}

/// What a dashboard shows.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DashboardSpec {
    pub dashboard_id: String,
    pub title: String,
    /// `(panel title, metric name, unit)`.
    pub panels: Vec<(String, String, String)>,
    pub refresh_seconds: u32,
}

/// Takes measurements.
pub trait MetricSink {
    fn record(&mut self, sample: MetricSample) -> Result<(), String>;
    fn series(&self, name: &str) -> Vec<MetricSample>;
}

/// Measurements in memory.
#[derive(Debug, Default)]
pub struct InMemoryMetricSink {
    samples: Vec<MetricSample>,
    max_samples: usize,
}

impl InMemoryMetricSink {
    pub fn new(max_samples: usize) -> Self {
        Self {
            samples: Vec::new(),
            max_samples: if max_samples == 0 { 10_000 } else { max_samples },
        }
    }
}

impl MetricSink for InMemoryMetricSink {
    fn record(&mut self, sample: MetricSample) -> Result<(), String> {
        if sample.name.trim().is_empty() {
            return Err("a metric needs a name".into());
        }
        if sample.looks_high_cardinality() {
            return Err(format!(
                "'{}' carries labels that look like identifiers; that would explode the series and may carry personal data",
                sample.name
            ));
        }
        self.samples.push(sample);
        while self.samples.len() > self.max_samples {
            self.samples.remove(0);
        }
        Ok(())
    }

    fn series(&self, name: &str) -> Vec<MetricSample> {
        let mut out: Vec<MetricSample> =
            self.samples.iter().filter(|s| s.name == name).cloned().collect();
        out.sort_by_key(|s| s.at_ms);
        out
    }
}

/// Takes nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMetricSink;

impl MetricSink for NullMetricSink {
    /// SUCCEEDS. Metrics are not the work, and failing a request because a
    /// counter had nowhere to go would be the tail wagging the dog.
    fn record(&mut self, _sample: MetricSample) -> Result<(), String> {
        Ok(())
    }
    fn series(&self, _name: &str) -> Vec<MetricSample> {
        Vec::new()
    }
}

/// Takes spans.
pub trait TraceSink {
    fn record(&mut self, span: TraceSpan);
    fn trace(&self, trace_id: &str) -> Vec<TraceSpan>;
    /// Spans whose parent never arrived.
    fn orphans(&self) -> Vec<TraceSpan>;
}

/// Spans in memory.
#[derive(Debug, Default)]
pub struct InMemoryTraceSink {
    spans: Vec<TraceSpan>,
}

impl InMemoryTraceSink {
    pub fn new() -> Self {
        Self::default()
    }
}

impl TraceSink for InMemoryTraceSink {
    fn record(&mut self, span: TraceSpan) {
        if !span.trace_id.is_empty() && !span.span_id.is_empty() {
            self.spans.push(span);
        }
    }

    fn trace(&self, trace_id: &str) -> Vec<TraceSpan> {
        let mut out: Vec<TraceSpan> =
            self.spans.iter().filter(|s| s.trace_id == trace_id).cloned().collect();
        out.sort_by_key(|s| s.started_at_ms);
        out
    }

    fn orphans(&self) -> Vec<TraceSpan> {
        let known: HashSet<&String> = self.spans.iter().map(|s| &s.span_id).collect();
        self.spans
            .iter()
            .filter(|s| !s.parent_span_id.is_empty() && !known.contains(&s.parent_span_id))
            .cloned()
            .collect()
    }
}

/// Takes no spans.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullTraceSink;

impl TraceSink for NullTraceSink {
    fn record(&mut self, _span: TraceSpan) {}
    fn trace(&self, _trace_id: &str) -> Vec<TraceSpan> {
        Vec::new()
    }
    fn orphans(&self) -> Vec<TraceSpan> {
        Vec::new()
    }
}

/// Publishes dashboards.
pub trait DashboardPublisher {
    fn publish(&mut self, spec: DashboardSpec) -> Result<String, String>;
    fn get(&self, dashboard_id: &str) -> Option<DashboardSpec>;
}

/// Dashboards in memory.
#[derive(Debug, Default)]
pub struct InMemoryDashboardPublisher {
    dashboards: HashMap<String, DashboardSpec>,
}

impl InMemoryDashboardPublisher {
    pub fn new() -> Self {
        Self::default()
    }
}

impl DashboardPublisher for InMemoryDashboardPublisher {
    fn publish(&mut self, spec: DashboardSpec) -> Result<String, String> {
        if spec.dashboard_id.trim().is_empty() {
            return Err("a dashboard needs an identifier".into());
        }
        if spec.panels.is_empty() {
            return Err("a dashboard with no panels shows nothing".into());
        }
        let id = spec.dashboard_id.clone();
        self.dashboards.insert(id.clone(), spec);
        Ok(id)
    }

    fn get(&self, dashboard_id: &str) -> Option<DashboardSpec> {
        self.dashboards.get(dashboard_id).cloned()
    }
}

/// Publishes nowhere.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDashboardPublisher;

impl DashboardPublisher for NullDashboardPublisher {
    fn publish(&mut self, _spec: DashboardSpec) -> Result<String, String> {
        Err("no dashboard service is configured on this device".into())
    }
    fn get(&self, _dashboard_id: &str) -> Option<DashboardSpec> {
        None
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Observer

/// Something a sensor said.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SensorReading {
    pub sensor: String,
    pub value: f64,
    pub unit: String,
    pub at_ms: u64,
    /// 0..1. `None` means the sensor did not say - which is honest, and better
    /// than a confident 1.0 nobody earned.
    pub confidence: Option<f32>,
}

/// Reads something about the world.
pub trait Sensor {
    fn name(&self) -> &str;
    fn is_available(&self) -> bool;
    fn read(&self, now_ms: u64) -> Option<SensorReading>;
}

/// Reads nothing.
///
/// Returns `None` rather than a zero reading. A zero from a temperature sensor
/// is a real value and would be acted on; absence is not.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSensor;

impl Sensor for NullSensor {
    fn name(&self) -> &str {
        "none"
    }
    fn is_available(&self) -> bool {
        false
    }
    fn read(&self, _now_ms: u64) -> Option<SensorReading> {
        None
    }
}

/// Keeps what sensors said.
#[derive(Debug, Default)]
pub struct SensorRecorder {
    readings: Vec<SensorReading>,
    max_readings: usize,
}

impl SensorRecorder {
    pub fn new(max_readings: usize) -> Self {
        Self {
            readings: Vec::new(),
            max_readings: if max_readings == 0 { 1000 } else { max_readings },
        }
    }

    pub fn record(&mut self, reading: SensorReading) {
        self.readings.push(reading);
        while self.readings.len() > self.max_readings {
            self.readings.remove(0);
        }
    }

    pub fn latest(&self, sensor: &str) -> Option<SensorReading> {
        self.readings
            .iter()
            .filter(|r| r.sensor == sensor)
            .max_by_key(|r| r.at_ms)
            .cloned()
    }

    /// The mean over a window, weighted by CONFIDENCE where it was given.
    ///
    /// An unweighted mean lets a reading the sensor itself doubted pull the
    /// answer as hard as one it was sure of.
    pub fn average(&self, sensor: &str, since_ms: u64) -> Option<f64> {
        let (sum, weight) = self
            .readings
            .iter()
            .filter(|r| r.sensor == sensor && r.at_ms >= since_ms)
            .fold((0.0, 0.0), |(sum, weight), r| {
                let w = r.confidence.unwrap_or(1.0) as f64;
                (sum + r.value * w, weight + w)
            });
        (weight > 0.0).then_some(sum / weight)
    }
}

/// One pass of the observation loop.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ObservationTick {
    pub tick: u64,
    pub at_ms: u64,
    pub readings: Vec<SensorReading>,
    pub notes: Vec<String>,
}

/// What the observer decided to do about a tick.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ObserverDecision {
    pub action: String,
    pub reason: String,
    /// Whether to tell somebody. MOST TICKS DO NOT - an observer that speaks
    /// every time it looks is one people turn off, and then it observes nothing.
    pub notify: bool,
    pub at_ms: u64,
}

/// A tool the observer may use.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ObservationTool {
    pub name: String,
    pub description: String,
    /// Whether it changes anything. A read-only tool can run unattended; one
    /// that acts cannot.
    pub read_only: bool,
}

/// The tools available.
pub trait ObservationToolbox {
    fn tools(&self) -> Vec<ObservationTool>;
    fn invoke(&self, name: &str, argument: &str) -> Result<String, String>;
}

/// A toolbox in memory.
///
/// REFUSES ANYTHING THAT ACTS unless it was told acting is allowed. An observer
/// is by default a thing that watches.
pub struct InMemoryObservationToolbox {
    tools: Vec<ObservationTool>,
    #[allow(clippy::type_complexity)]
    run: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    allow_acting: bool,
}

impl InMemoryObservationToolbox {
    #[allow(clippy::type_complexity)]
    pub fn new(
        tools: Vec<ObservationTool>,
        run: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
        allow_acting: bool,
    ) -> Self {
        Self { tools, run, allow_acting }
    }
}

impl ObservationToolbox for InMemoryObservationToolbox {
    fn tools(&self) -> Vec<ObservationTool> {
        self.tools
            .iter()
            .filter(|t| self.allow_acting || t.read_only)
            .cloned()
            .collect()
    }

    fn invoke(&self, name: &str, argument: &str) -> Result<String, String> {
        let Some(tool) = self.tools.iter().find(|t| t.name == name) else {
            return Err(format!("there is no tool called '{name}'"));
        };
        if !tool.read_only && !self.allow_acting {
            return Err(format!(
                "'{name}' changes things, and this observer is only allowed to watch"
            ));
        }
        let Some(run) = &self.run else {
            return Err("no tool runner is configured".into());
        };
        run(name, argument)
    }
}

/// Watches, and decides.
pub trait ObservationLoop {
    fn is_running(&self) -> bool;
    fn tick(&mut self, now_ms: u64) -> (ObservationTick, Vec<ObserverDecision>);
    fn stop(&mut self);
}

/// A loop in memory.
pub struct InMemoryObservationLoop {
    sensors: Vec<Box<dyn Sensor + Send + Sync>>,
    recorder: SensorRecorder,
    #[allow(clippy::type_complexity)]
    decide: Option<Box<dyn Fn(&ObservationTick) -> Vec<ObserverDecision> + Send + Sync>>,
    running: bool,
    count: u64,
    /// The last time it told somebody anything. Used to keep it quiet.
    last_notified_ms: u64,
    min_notify_interval_ms: u64,
}

impl InMemoryObservationLoop {
    #[allow(clippy::type_complexity)]
    pub fn new(
        sensors: Vec<Box<dyn Sensor + Send + Sync>>,
        decide: Option<Box<dyn Fn(&ObservationTick) -> Vec<ObserverDecision> + Send + Sync>>,
        min_notify_interval_ms: u64,
    ) -> Self {
        Self {
            sensors,
            recorder: SensorRecorder::new(0),
            decide,
            running: true,
            count: 0,
            last_notified_ms: 0,
            min_notify_interval_ms: if min_notify_interval_ms == 0 {
                300_000
            } else {
                min_notify_interval_ms
            },
        }
    }

    pub fn recorder(&self) -> &SensorRecorder {
        &self.recorder
    }
}

impl ObservationLoop for InMemoryObservationLoop {
    fn is_running(&self) -> bool {
        self.running
    }

    fn tick(&mut self, now_ms: u64) -> (ObservationTick, Vec<ObserverDecision>) {
        if !self.running {
            return (ObservationTick::default(), Vec::new());
        }
        self.count += 1;

        let mut readings = Vec::new();
        let mut notes = Vec::new();
        for sensor in &self.sensors {
            match sensor.read(now_ms) {
                Some(reading) => readings.push(reading),
                // An unavailable sensor is NOTED rather than silently skipped. A
                // loop that quietly observes nothing looks identical to one
                // observing everything and finding nothing wrong.
                None => notes.push(format!("{} did not answer", sensor.name())),
            }
        }
        for reading in &readings {
            self.recorder.record(reading.clone());
        }

        let tick = ObservationTick { tick: self.count, at_ms: now_ms, readings, notes };
        let mut decisions = self.decide.as_ref().map(|d| d(&tick)).unwrap_or_default();

        // Notifications are rate-limited HERE rather than at each caller, so
        // there is one place that decides how often this thing is allowed to
        // interrupt somebody.
        if now_ms.saturating_sub(self.last_notified_ms) < self.min_notify_interval_ms {
            for decision in decisions.iter_mut() {
                decision.notify = false;
            }
        } else if decisions.iter().any(|d| d.notify) {
            self.last_notified_ms = now_ms;
        }

        (tick, decisions)
    }

    fn stop(&mut self) {
        self.running = false;
    }
}

/// Watches nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullObservationLoop;

impl ObservationLoop for NullObservationLoop {
    fn is_running(&self) -> bool {
        false
    }
    fn tick(&mut self, _now_ms: u64) -> (ObservationTick, Vec<ObserverDecision>) {
        (ObservationTick::default(), Vec::new())
    }
    fn stop(&mut self) {}
}

// ─────────────────────────────────────────────────────────────────────────────
// Spec-driven development

/// What somebody wants built.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Specification {
    pub spec_id: String,
    pub title: String,
    pub summary: String,
    /// What must be true when it is done. In OBSERVABLE terms - "the list loads
    /// in under a second" can be checked; "the list is fast" cannot.
    pub acceptance: Vec<String>,
    /// What is deliberately NOT being built. The half of a spec that prevents
    /// the most rework.
    pub non_goals: Vec<String>,
    pub version: u32,
}

/// What a validator made of it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SpecValidationResult {
    pub valid: bool,
    pub errors: Vec<String>,
    /// Things worth fixing that do not block. Kept apart from errors so a
    /// warning cannot quietly stop a build.
    pub warnings: Vec<String>,
}

/// What was scaffolded from one.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ScaffoldedProject {
    pub name: String,
    /// Path to content.
    pub files: HashMap<String, String>,
    pub entry_point: String,
    pub notes: Vec<String>,
}

impl ScaffoldedProject {
    pub fn file_count(&self) -> usize {
        self.files.len()
    }
}

/// Keeps specifications.
pub trait SpecificationStore {
    fn put(&mut self, spec: Specification) -> Result<u32, String>;
    fn get(&self, spec_id: &str) -> Option<Specification>;
    /// Every version, oldest first. A spec's HISTORY is what tells you when a
    /// requirement appeared, which is the question asked when something is found
    /// to be missing.
    fn history(&self, spec_id: &str) -> Vec<Specification>;
}

/// Specs in memory.
#[derive(Debug, Default)]
pub struct InMemorySpecificationStore {
    versions: HashMap<String, Vec<Specification>>,
}

impl InMemorySpecificationStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl SpecificationStore for InMemorySpecificationStore {
    /// APPENDS a version rather than replacing. Overwriting a spec loses the
    /// only record of what was agreed before it changed.
    fn put(&mut self, mut spec: Specification) -> Result<u32, String> {
        if spec.spec_id.trim().is_empty() {
            return Err("a specification needs an identifier".into());
        }
        let versions = self.versions.entry(spec.spec_id.clone()).or_default();
        spec.version = versions.len() as u32 + 1;
        let version = spec.version;
        versions.push(spec);
        Ok(version)
    }

    fn get(&self, spec_id: &str) -> Option<Specification> {
        self.versions.get(spec_id)?.last().cloned()
    }

    fn history(&self, spec_id: &str) -> Vec<Specification> {
        self.versions.get(spec_id).cloned().unwrap_or_default()
    }
}

/// Keeps none.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSpecificationStore;

impl SpecificationStore for NullSpecificationStore {
    fn put(&mut self, _spec: Specification) -> Result<u32, String> {
        Err("no specification store is configured".into())
    }
    fn get(&self, _spec_id: &str) -> Option<Specification> {
        None
    }
    fn history(&self, _spec_id: &str) -> Vec<Specification> {
        Vec::new()
    }
}

/// Checks a specification.
pub trait SpecificationValidator {
    fn validate(&self, spec: &Specification) -> SpecValidationResult;
}

/// Checks the shape.
///
/// SHAPE ONLY, and it says so: whether a specification is a good one is a
/// judgement, and a validator that claimed to make it would be trusted for
/// something it cannot do.
#[derive(Debug, Default, Clone, Copy)]
pub struct JsonShapeSpecificationValidator;

impl SpecificationValidator for JsonShapeSpecificationValidator {
    fn validate(&self, spec: &Specification) -> SpecValidationResult {
        let mut errors = Vec::new();
        let mut warnings = Vec::new();

        if spec.title.trim().is_empty() {
            errors.push("a specification needs a title".into());
        }
        if spec.acceptance.is_empty() {
            errors.push(
                "a specification with no acceptance criteria cannot be shown to be finished".into(),
            );
        }
        if spec.non_goals.is_empty() {
            warnings.push(
                "nothing is listed as out of scope, which usually means scope has not been discussed"
                    .into(),
            );
        }
        for criterion in &spec.acceptance {
            // Vague criteria pass a validator and fail a review. Naming them as
            // warnings is the honest middle: they are not malformed, they are
            // unmeasurable.
            if ["fast", "easy", "intuitive", "nice", "good", "simple"]
                .iter()
                .any(|w| criterion.to_lowercase().split_whitespace().any(|t| t == *w))
            {
                warnings.push(format!("'{criterion}' cannot be checked as written"));
            }
        }

        SpecValidationResult { valid: errors.is_empty(), errors, warnings }
    }
}

/// Checks nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSpecificationValidator;

impl SpecificationValidator for NullSpecificationValidator {
    /// INVALID, not valid. A validator that approves everything is worse than
    /// none, because callers believe it.
    fn validate(&self, _spec: &Specification) -> SpecValidationResult {
        SpecValidationResult {
            valid: false,
            errors: vec!["no specification validator is configured, so nothing was checked".into()],
            warnings: Vec::new(),
        }
    }
}

/// Turns a specification into a project.
pub trait SpecToScaffold {
    fn scaffold(&self, spec: &Specification) -> Result<ScaffoldedProject, String>;
}

/// Scaffolds something that runs.
///
/// A HELLO WORLD THAT ACTUALLY RUNS beats a directory of empty files: the first
/// thing anybody does with a scaffold is run it, and one that does not is a
/// scaffold nobody trusts.
#[derive(Debug, Default, Clone, Copy)]
pub struct HelloWorldSpecToScaffold;

impl SpecToScaffold for HelloWorldSpecToScaffold {
    fn scaffold(&self, spec: &Specification) -> Result<ScaffoldedProject, String> {
        if spec.title.trim().is_empty() {
            return Err("a project needs a name, which comes from the specification title".into());
        }
        let name: String = spec
            .title
            .to_lowercase()
            .chars()
            .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
            .collect::<String>()
            .split('-')
            .filter(|s| !s.is_empty())
            .collect::<Vec<_>>()
            .join("-");

        let mut files = HashMap::new();
        files.insert(
            "README.md".to_string(),
            format!(
                "# {}\n\n{}\n\n## Done when\n\n{}\n\n## Not doing\n\n{}\n",
                spec.title,
                spec.summary,
                spec.acceptance
                    .iter()
                    .map(|a| format!("- [ ] {a}"))
                    .collect::<Vec<_>>()
                    .join("\n"),
                if spec.non_goals.is_empty() {
                    "- (nothing agreed yet)".to_string()
                } else {
                    spec.non_goals
                        .iter()
                        .map(|g| format!("- {g}"))
                        .collect::<Vec<_>>()
                        .join("\n")
                }
            ),
        );
        files.insert(
            "src/main.rs".to_string(),
            format!("fn main() {{\n    println!(\"{name} is running\");\n}}\n"),
        );

        Ok(ScaffoldedProject {
            name: name.clone(),
            files,
            entry_point: "src/main.rs".into(),
            // The acceptance criteria travel INTO the scaffold as a checklist.
            // A spec that stops at the door is a spec nobody reads again.
            notes: spec.acceptance.clone(),
        })
    }
}

/// Scaffolds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSpecToScaffold;

impl SpecToScaffold for NullSpecToScaffold {
    fn scaffold(&self, _spec: &Specification) -> Result<ScaffoldedProject, String> {
        Err("no scaffolder is configured on this device".into())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Visualization

/// A dashboard somebody defined.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DashboardDefinition {
    pub id: String,
    pub title: String,
    /// `(panel title, kind, query)`.
    pub panels: Vec<(String, String, String)>,
    pub owner: String,
}

/// Documentation for an interface.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ApiDoc {
    pub title: String,
    pub version: String,
    /// `(method, path, summary)`.
    pub operations: Vec<(String, String, String)>,
    pub base_url: String,
}

impl ApiDoc {
    /// Grouped by path, so the page reads as resources rather than a flat list
    /// of verbs.
    pub fn by_path(&self) -> Vec<(String, Vec<(String, String)>)> {
        let mut grouped: HashMap<String, Vec<(String, String)>> = HashMap::new();
        for (method, path, summary) in &self.operations {
            grouped
                .entry(path.clone())
                .or_default()
                .push((method.clone(), summary.clone()));
        }
        let mut out: Vec<(String, Vec<(String, String)>)> = grouped.into_iter().collect();
        out.sort_by(|a, b| a.0.cmp(&b.0));
        out
    }
}

/// A site that was built.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct GeneratedSite {
    pub files: HashMap<String, String>,
    pub entry_page: String,
    pub bytes: usize,
}

/// Keeps dashboard definitions.
pub trait DashboardDefinitionStore {
    fn put(&mut self, definition: DashboardDefinition) -> Result<(), String>;
    fn get(&self, id: &str) -> Option<DashboardDefinition>;
    fn list(&self) -> Vec<DashboardDefinition>;
}

/// Definitions in memory.
#[derive(Debug, Default)]
pub struct InMemoryDashboardStore {
    definitions: HashMap<String, DashboardDefinition>,
}

impl InMemoryDashboardStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl DashboardDefinitionStore for InMemoryDashboardStore {
    fn put(&mut self, definition: DashboardDefinition) -> Result<(), String> {
        if definition.id.trim().is_empty() {
            return Err("a dashboard needs an identifier".into());
        }
        self.definitions.insert(definition.id.clone(), definition);
        Ok(())
    }

    fn get(&self, id: &str) -> Option<DashboardDefinition> {
        self.definitions.get(id).cloned()
    }

    fn list(&self) -> Vec<DashboardDefinition> {
        let mut out: Vec<DashboardDefinition> = self.definitions.values().cloned().collect();
        out.sort_by(|a, b| a.title.cmp(&b.title));
        out
    }
}

/// Keeps none.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDashboardDefinitionStore;

impl DashboardDefinitionStore for NullDashboardDefinitionStore {
    fn put(&mut self, _definition: DashboardDefinition) -> Result<(), String> {
        Err("no dashboard store is configured".into())
    }
    fn get(&self, _id: &str) -> Option<DashboardDefinition> {
        None
    }
    fn list(&self) -> Vec<DashboardDefinition> {
        Vec::new()
    }
}

/// Builds documentation.
pub trait ApiDocBuilder {
    fn build(&self, doc: &ApiDoc) -> Result<String, String>;
}

/// Builds it as JSON.
#[derive(Debug, Default, Clone, Copy)]
pub struct JsonApiDocBuilder;

impl JsonApiDocBuilder {
    /// Escapes a JSON string. Written out rather than pulled in, because a JSON
    /// dependency for one function is a build cost on every target.
    ///
    /// CONTROL CHARACTERS ARE ESCAPED TOO. A tab or newline inside a summary
    /// produces invalid JSON that parses in some readers and not others - the
    /// kind of bug found by a user rather than a test.
    pub fn escape(value: &str) -> String {
        let mut out = String::with_capacity(value.len() + 2);
        for c in value.chars() {
            match c {
                '"' => out.push_str("\\\""),
                '\\' => out.push_str("\\\\"),
                '\n' => out.push_str("\\n"),
                '\r' => out.push_str("\\r"),
                '\t' => out.push_str("\\t"),
                c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
                c => out.push(c),
            }
        }
        out
    }
}

impl ApiDocBuilder for JsonApiDocBuilder {
    fn build(&self, doc: &ApiDoc) -> Result<String, String> {
        if doc.title.trim().is_empty() {
            return Err("documentation needs a title".into());
        }
        let operations = doc
            .operations
            .iter()
            .map(|(method, path, summary)| {
                format!(
                    "{{\"method\":\"{}\",\"path\":\"{}\",\"summary\":\"{}\"}}",
                    Self::escape(&method.to_uppercase()),
                    Self::escape(path),
                    Self::escape(summary)
                )
            })
            .collect::<Vec<_>>()
            .join(",");
        Ok(format!(
            "{{\"title\":\"{}\",\"version\":\"{}\",\"baseUrl\":\"{}\",\"operations\":[{operations}]}}",
            Self::escape(&doc.title),
            Self::escape(&doc.version),
            Self::escape(&doc.base_url)
        ))
    }
}

/// Builds nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullApiDocBuilder;

impl ApiDocBuilder for NullApiDocBuilder {
    fn build(&self, _doc: &ApiDoc) -> Result<String, String> {
        Err("no documentation builder is configured".into())
    }
}

/// Builds a site.
pub trait SiteBuilder {
    fn build(&self, doc: &ApiDoc, dashboards: &[DashboardDefinition]) -> GeneratedSite;
}

/// Builds a static one.
///
/// NO SCRIPTS AND NO EXTERNAL REQUESTS. A documentation page that fetches a font
/// tells a third party who read the documentation, and a page that runs script
/// is a page that can do more than document.
#[derive(Debug, Default, Clone, Copy)]
pub struct StaticSiteBuilder;

impl StaticSiteBuilder {
    /// Escapes for HTML. The ampersand FIRST - escaping it after the others
    /// would double-escape the entities just introduced.
    pub fn escape_html(value: &str) -> String {
        value
            .replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;")
            .replace('"', "&quot;")
    }
}

impl SiteBuilder for StaticSiteBuilder {
    fn build(&self, doc: &ApiDoc, dashboards: &[DashboardDefinition]) -> GeneratedSite {
        let mut body = format!(
            "<h1>{}</h1><p>version {}</p>",
            Self::escape_html(&doc.title),
            Self::escape_html(&doc.version)
        );
        for (path, operations) in doc.by_path() {
            body.push_str(&format!("<h2><code>{}</code></h2><ul>", Self::escape_html(&path)));
            for (method, summary) in operations {
                body.push_str(&format!(
                    "<li><strong>{}</strong> {}</li>",
                    Self::escape_html(&method.to_uppercase()),
                    Self::escape_html(&summary)
                ));
            }
            body.push_str("</ul>");
        }
        if !dashboards.is_empty() {
            body.push_str("<h2>Dashboards</h2><ul>");
            for dashboard in dashboards {
                body.push_str(&format!(
                    "<li>{} ({} panels)</li>",
                    Self::escape_html(&dashboard.title),
                    dashboard.panels.len()
                ));
            }
            body.push_str("</ul>");
        }

        let page = format!(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">\
<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\
<title>{}</title><style>body{{font-family:system-ui,sans-serif;max-width:44rem;\
margin:2rem auto;padding:0 1rem;line-height:1.6}}code{{background:#0001;padding:.1em .3em}}\
</style></head><body>{body}</body></html>",
            Self::escape_html(&doc.title)
        );

        GeneratedSite {
            bytes: page.len(),
            files: HashMap::from([("index.html".to_string(), page)]),
            entry_page: "index.html".into(),
        }
    }
}

/// Builds no site.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSiteBuilder;

impl SiteBuilder for NullSiteBuilder {
    fn build(&self, _doc: &ApiDoc, _dashboards: &[DashboardDefinition]) -> GeneratedSite {
        GeneratedSite::default()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Research

/// A paper.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ResearchPaper {
    pub paper_id: String,
    pub title: String,
    pub authors: Vec<String>,
    pub year: u32,
    pub venue: String,
    pub abstract_text: String,
    /// The licence of the TEXT. A paper that may be read is not necessarily a
    /// paper whose text may be redistributed, and a corpus that ignores the
    /// difference redistributes it anyway.
    pub licence: String,
    pub url: String,
}

impl ResearchPaper {
    /// Whether the full text may be kept.
    ///
    /// Open licences ONLY, matched conservatively: an unrecognised licence is
    /// treated as no, because the cost of guessing wrong falls on the author.
    pub fn is_redistributable(&self) -> bool {
        let licence = self.licence.to_lowercase();
        ["cc-by", "cc0", "public domain", "mit", "apache-2.0", "bsd"]
            .iter()
            .any(|l| licence.contains(l))
    }

    /// "Author et al., year". Three or more authors get et al., which is the
    /// convention nearly everywhere.
    pub fn short_citation(&self) -> String {
        let who = match self.authors.len() {
            0 => "Anon".to_string(),
            1 => self.authors[0].clone(),
            2 => format!("{} and {}", self.authors[0], self.authors[1]),
            _ => format!("{} et al.", self.authors[0]),
        };
        format!("{who}, {}", self.year)
    }
}

/// One paper citing another.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct Citation {
    pub from_paper: String,
    pub to_paper: String,
    pub context: String,
}

/// Holds papers.
pub trait ResearchCorpus {
    fn add(&mut self, paper: ResearchPaper) -> Result<(), String>;
    fn get(&self, paper_id: &str) -> Option<ResearchPaper>;
    fn search(&self, query: &str, limit: usize) -> Vec<ResearchPaper>;
}

/// A corpus in memory.
#[derive(Debug, Default)]
pub struct InMemoryResearchCorpus {
    papers: HashMap<String, ResearchPaper>,
}

impl InMemoryResearchCorpus {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ResearchCorpus for InMemoryResearchCorpus {
    /// Keeps the METADATA of anything and the TEXT of only what may be kept.
    ///
    /// Title, authors and a link are facts about a paper; the abstract is the
    /// author's writing. Dropping it where the licence does not allow keeps the
    /// corpus useful without redistributing what is not ours.
    fn add(&mut self, mut paper: ResearchPaper) -> Result<(), String> {
        if paper.paper_id.trim().is_empty() {
            return Err("a paper needs an identifier".into());
        }
        if !paper.is_redistributable() {
            paper.abstract_text.clear();
        }
        self.papers.insert(paper.paper_id.clone(), paper);
        Ok(())
    }

    fn get(&self, paper_id: &str) -> Option<ResearchPaper> {
        self.papers.get(paper_id).cloned()
    }

    fn search(&self, query: &str, limit: usize) -> Vec<ResearchPaper> {
        let needle = query.to_lowercase();
        if needle.trim().is_empty() {
            return Vec::new();
        }
        let mut hits: Vec<ResearchPaper> = self
            .papers
            .values()
            .filter(|p| {
                p.title.to_lowercase().contains(&needle)
                    || p.abstract_text.to_lowercase().contains(&needle)
                    || p.authors.iter().any(|a| a.to_lowercase().contains(&needle))
            })
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.year.cmp(&a.year).then_with(|| a.title.cmp(&b.title)));
        hits.truncate(if limit == 0 { 20 } else { limit });
        hits
    }
}

/// Holds none.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullResearchCorpus;

impl ResearchCorpus for NullResearchCorpus {
    fn add(&mut self, _paper: ResearchPaper) -> Result<(), String> {
        Err("no research corpus is configured on this device".into())
    }
    fn get(&self, _paper_id: &str) -> Option<ResearchPaper> {
        None
    }
    fn search(&self, _query: &str, _limit: usize) -> Vec<ResearchPaper> {
        Vec::new()
    }
}

/// Fetches papers.
pub trait PaperRetrieval {
    fn is_available(&self) -> bool;
    fn fetch(&self, paper_id: &str) -> Result<ResearchPaper, String>;
}

/// Fetches from what is already held.
///
/// Offline by design: reaching out to fetch a paper tells whoever serves it that
/// somebody here is reading it, which is a fact about a person's research.
pub struct InMemoryPaperRetrieval {
    papers: HashMap<String, ResearchPaper>,
}

impl InMemoryPaperRetrieval {
    pub fn new(papers: Vec<ResearchPaper>) -> Self {
        Self {
            papers: papers.into_iter().map(|p| (p.paper_id.clone(), p)).collect(),
        }
    }
}

impl PaperRetrieval for InMemoryPaperRetrieval {
    fn is_available(&self) -> bool {
        !self.papers.is_empty()
    }
    fn fetch(&self, paper_id: &str) -> Result<ResearchPaper, String> {
        self.papers
            .get(paper_id)
            .cloned()
            .ok_or_else(|| format!("'{paper_id}' is not held on this device"))
    }
}

/// Fetches nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPaperRetrieval;

impl PaperRetrieval for NullPaperRetrieval {
    fn is_available(&self) -> bool {
        false
    }
    fn fetch(&self, _paper_id: &str) -> Result<ResearchPaper, String> {
        Err("no paper source is configured; nothing was requested from anyone".into())
    }
}

/// What cites what.
pub trait CitationGraph {
    fn add(&mut self, citation: Citation);
    fn cited_by(&self, paper_id: &str) -> Vec<Citation>;
    fn cites(&self, paper_id: &str) -> Vec<Citation>;
    /// Most-cited first.
    fn most_cited(&self, limit: usize) -> Vec<(String, usize)>;
}

/// A citation graph in memory.
#[derive(Debug, Default)]
pub struct InMemoryCitationGraph {
    citations: Vec<Citation>,
}

impl InMemoryCitationGraph {
    pub fn new() -> Self {
        Self::default()
    }
}

impl CitationGraph for InMemoryCitationGraph {
    fn add(&mut self, citation: Citation) {
        // A paper citing itself is a data error, not a citation, and it skews
        // every count downstream.
        if citation.from_paper.is_empty()
            || citation.to_paper.is_empty()
            || citation.from_paper == citation.to_paper
        {
            return;
        }
        if !self.citations.contains(&citation) {
            self.citations.push(citation);
        }
    }

    fn cited_by(&self, paper_id: &str) -> Vec<Citation> {
        self.citations
            .iter()
            .filter(|c| c.to_paper == paper_id)
            .cloned()
            .collect()
    }

    fn cites(&self, paper_id: &str) -> Vec<Citation> {
        self.citations
            .iter()
            .filter(|c| c.from_paper == paper_id)
            .cloned()
            .collect()
    }

    fn most_cited(&self, limit: usize) -> Vec<(String, usize)> {
        let mut counts: HashMap<&str, usize> = HashMap::new();
        for citation in &self.citations {
            *counts.entry(citation.to_paper.as_str()).or_insert(0) += 1;
        }
        let mut out: Vec<(String, usize)> =
            counts.into_iter().map(|(k, v)| (k.to_string(), v)).collect();
        out.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
        out.truncate(if limit == 0 { 10 } else { limit });
        out
    }
}

/// An empty graph.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCitationGraph;

impl CitationGraph for NullCitationGraph {
    fn add(&mut self, _citation: Citation) {}
    fn cited_by(&self, _paper_id: &str) -> Vec<Citation> {
        Vec::new()
    }
    fn cites(&self, _paper_id: &str) -> Vec<Citation> {
        Vec::new()
    }
    fn most_cited(&self, _limit: usize) -> Vec<(String, usize)> {
        Vec::new()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Micro-agents

/// What a small agent is for.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MicroAgentDescriptor {
    pub name: String,
    pub purpose: String,
    /// When to reach for it. In the words somebody would use asking, so the
    /// match is against intent rather than jargon.
    pub triggers: Vec<String>,
    /// Whether it changes anything.
    pub read_only: bool,
    /// A rough cost, so a host can prefer the cheap one when both fit.
    pub typical_ms: u64,
}

/// What one said.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MicroAgentResponse {
    pub agent: String,
    pub text: String,
    pub succeeded: bool,
    /// Set when it refused. SEPARATE from an error, because a refusal is a
    /// working agent doing its job and an error is not.
    pub refusal: String,
    pub took_ms: u64,
}

impl MicroAgentResponse {
    pub fn refused(agent: &str, reason: &str) -> Self {
        Self {
            agent: agent.to_string(),
            succeeded: false,
            refusal: reason.to_string(),
            ..Default::default()
        }
    }
}

/// A small agent.
pub trait MicroAgent {
    fn descriptor(&self) -> MicroAgentDescriptor;
    fn handle(&self, request: &str) -> MicroAgentResponse;
}

/// One built from a function.
pub struct FuncMicroAgent {
    descriptor: MicroAgentDescriptor,
    handler: Box<dyn Fn(&str) -> String + Send + Sync>,
}

impl FuncMicroAgent {
    pub fn new(
        descriptor: MicroAgentDescriptor,
        handler: Box<dyn Fn(&str) -> String + Send + Sync>,
    ) -> Self {
        Self { descriptor, handler }
    }
}

impl MicroAgent for FuncMicroAgent {
    fn descriptor(&self) -> MicroAgentDescriptor {
        self.descriptor.clone()
    }

    fn handle(&self, request: &str) -> MicroAgentResponse {
        let text = (self.handler)(request);
        MicroAgentResponse {
            agent: self.descriptor.name.clone(),
            succeeded: !text.is_empty(),
            text,
            refusal: String::new(),
            took_ms: 0,
        }
    }
}

/// Does nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMicroAgent;

impl MicroAgent for NullMicroAgent {
    fn descriptor(&self) -> MicroAgentDescriptor {
        MicroAgentDescriptor {
            name: "none".into(),
            purpose: "a placeholder that answers nothing".into(),
            read_only: true,
            ..Default::default()
        }
    }
    fn handle(&self, _request: &str) -> MicroAgentResponse {
        MicroAgentResponse::refused("none", "no agent is configured to answer that")
    }
}

/// One call to one agent.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MicroAgentInvocation {
    pub agent: String,
    pub request: String,
    pub response: MicroAgentResponse,
    pub at_ms: u64,
}

/// What has been asked of whom.
#[derive(Debug, Default)]
pub struct MicroAgentInvocationLog {
    invocations: Vec<MicroAgentInvocation>,
    max_entries: usize,
}

impl MicroAgentInvocationLog {
    pub fn new(max_entries: usize) -> Self {
        Self {
            invocations: Vec::new(),
            max_entries: if max_entries == 0 { 200 } else { max_entries },
        }
    }

    pub fn record(&mut self, invocation: MicroAgentInvocation) {
        self.invocations.push(invocation);
        while self.invocations.len() > self.max_entries {
            self.invocations.remove(0);
        }
    }

    pub fn for_agent(&self, agent: &str) -> Vec<MicroAgentInvocation> {
        self.invocations
            .iter()
            .filter(|i| i.agent == agent)
            .cloned()
            .collect()
    }

    /// How often each agent refused.
    ///
    /// A high refusal rate is a ROUTING problem, not an agent one: it means work
    /// keeps arriving at an agent that was never right for it.
    pub fn refusal_rate(&self, agent: &str) -> f32 {
        let calls = self.for_agent(agent);
        if calls.is_empty() {
            return 0.0;
        }
        calls.iter().filter(|i| !i.response.refusal.is_empty()).count() as f32 / calls.len() as f32
    }
}

/// Finds the right agent.
#[derive(Debug, Default, Clone, Copy)]
pub struct MicroAgentSearch;

impl MicroAgentSearch {
    /// How well a descriptor fits a request.
    ///
    /// Trigger phrases count for more than the purpose text: a trigger is what
    /// somebody would actually say, and the purpose is how it was described to
    /// another developer.
    pub fn score(descriptor: &MicroAgentDescriptor, request: &str) -> f32 {
        let request = request.to_lowercase();
        let mut score = 0.0;
        for trigger in &descriptor.triggers {
            if request.contains(&trigger.to_lowercase()) {
                score += 1.0;
            }
        }
        for word in descriptor.purpose.to_lowercase().split_whitespace() {
            if word.len() > 4 && request.contains(word) {
                score += 0.2;
            }
        }
        score
    }

    /// The best fits, best first. Anything scoring zero is left out - a
    /// zero-scoring agent is not a weak match, it is not a match.
    pub fn rank(descriptors: &[MicroAgentDescriptor], request: &str) -> Vec<(String, f32)> {
        let mut out: Vec<(String, f32)> = descriptors
            .iter()
            .map(|d| (d.name.clone(), Self::score(d, request)))
            .filter(|(_, score)| *score > 0.0)
            .collect();
        out.sort_by(|a, b| {
            b.1.partial_cmp(&a.1)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.0.cmp(&b.0))
        });
        out
    }
}

/// Runs micro-agents.
pub trait MicroAgentHost {
    fn register(&mut self, agent: Box<dyn MicroAgent + Send + Sync>);
    fn descriptors(&self) -> Vec<MicroAgentDescriptor>;
    fn dispatch(&mut self, request: &str, now_ms: u64) -> MicroAgentResponse;
}

/// A host in memory.
pub struct InMemoryMicroAgentHost {
    agents: Vec<Box<dyn MicroAgent + Send + Sync>>,
    log: MicroAgentInvocationLog,
    /// Whether agents that change things may be dispatched to automatically.
    allow_acting: bool,
}

impl InMemoryMicroAgentHost {
    pub fn new(allow_acting: bool) -> Self {
        Self { agents: Vec::new(), log: MicroAgentInvocationLog::new(0), allow_acting }
    }

    pub fn log(&self) -> &MicroAgentInvocationLog {
        &self.log
    }
}

impl MicroAgentHost for InMemoryMicroAgentHost {
    fn register(&mut self, agent: Box<dyn MicroAgent + Send + Sync>) {
        self.agents.push(agent);
    }

    fn descriptors(&self) -> Vec<MicroAgentDescriptor> {
        self.agents.iter().map(|a| a.descriptor()).collect()
    }

    fn dispatch(&mut self, request: &str, now_ms: u64) -> MicroAgentResponse {
        let ranked = MicroAgentSearch::rank(&self.descriptors(), request);
        let Some((name, _)) = ranked.first() else {
            return MicroAgentResponse::refused(
                "host",
                "nothing here handles that kind of request",
            );
        };
        let Some(agent) = self.agents.iter().find(|a| a.descriptor().name == *name) else {
            return MicroAgentResponse::refused("host", "that agent is no longer registered");
        };
        // An agent that ACTS is not dispatched to automatically unless this host
        // was told it may. Routing something to an agent that changes things, on
        // a keyword match, is how an assistant does something nobody asked for.
        if !agent.descriptor().read_only && !self.allow_acting {
            return MicroAgentResponse::refused(
                name,
                "that would change something, so it needs to be asked for directly",
            );
        }
        let response = agent.handle(request);
        self.log.record(MicroAgentInvocation {
            agent: name.clone(),
            request: request.to_string(),
            response: response.clone(),
            at_ms: now_ms,
        });
        response
    }
}
