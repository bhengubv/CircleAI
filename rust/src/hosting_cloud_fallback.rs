//! hosting_cloud_fallback — CircleAI.Hosting.CloudFallback (Rust port).
//!
//! Composite chat generators that provide resilience across an ordered set of
//! backends. Ported from `CloudFallbackChain.cs`, `BackupBrainOrchestrator.cs`,
//! and the `IConfigurableChatGenerator` contract in `CloudFallbackChain.cs`.
//! The concrete vendor generators (OpenAI / Groq / Cerebras / Together /
//! DeepSeek / Anthropic / Gemini) are pure HTTP clients — per the brief they
//! are injected behind [`ICloudChatGenerator`], with a deterministic local fake
//! ([`FakeCloudGenerator`]) shipping for tests.
//!
//! SYNC: the C# `IChatGenerator` streaming API is projected to a materialised
//! `Vec<String>` of chunks. The two composites differ, ported 1:1:
//!   * [`CloudFallbackChain`] — start-of-call ordering. Walks the list, uses the
//!     first *ready* generator, skips fail-soft "[… not configured]" frames.
//!   * [`BackupBrainOrchestrator`] — mid-run failover with a degraded/cool-down
//!     half-open state machine and per-turn retry budget.

use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

/// Object-safe cloud/chat generator used across the fallback layer. Mirrors
/// `CircleAI.Inference.IChatGenerator` projected to a sync, materialised-stream
/// shape. A generator that can't serve a call returns a fail-soft frame from
/// [`Self::stream`] rather than erroring.
pub trait ICloudChatGenerator: Send + Sync {
    /// Generate a complete reply (concatenation of the streamed chunks).
    fn generate(&self, messages: &[CloudChatMessage]) -> Result<String, String>;

    /// Stream the reply as chunks. On a decline (e.g. no API key) return a
    /// single fail-soft frame like `"[<provider> API key not configured.]"`.
    fn stream(&self, messages: &[CloudChatMessage]) -> Result<Vec<String>, String>;
}

/// (3.2.0) A configurable generator that reports whether it can serve calls.
/// 1:1 with the C# `IConfigurableChatGenerator`. On-device generators that don't
/// implement it are presumed always ready (the chain falls through on failure
/// anyway) — represented here by [`ICloudChatGenerator`] impls that don't also
/// implement this trait; use [`is_ready`] to test either.
pub trait IConfigurableChatGenerator: ICloudChatGenerator {
    /// `true` when the generator can serve calls (e.g. API key present).
    fn is_configured(&self) -> bool;
    /// Display name (e.g. `"OpenAI · gpt-4o-mini"`).
    fn engine_label(&self) -> String;
    /// Human-readable explanation of the current state.
    fn status_message(&self) -> String;
}

/// A minimal chat message for the cloud layer (role + content), independent of
/// the inference-layer type so this module has no cross-layer coupling.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CloudChatMessage {
    pub role: String,
    pub content: String,
}

impl CloudChatMessage {
    pub fn new(role: impl Into<String>, content: impl Into<String>) -> Self {
        Self {
            role: role.into(),
            content: content.into(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FakeCloudGenerator — deterministic local test double
// ─────────────────────────────────────────────────────────────────────────────

/// Deterministic [`IConfigurableChatGenerator`] for tests. When `configured`,
/// streams `chunks`; otherwise streams one fail-soft frame
/// (`"[<label> not configured]"`). Set `fail` to have every call error mid-run
/// (used to exercise failover paths).
pub struct FakeCloudGenerator {
    label: String,
    configured: bool,
    chunks: Vec<String>,
    fail: bool,
    calls: Mutex<u32>,
}

impl FakeCloudGenerator {
    /// A configured generator that streams the given chunks.
    pub fn ready(label: impl Into<String>, chunks: Vec<String>) -> Self {
        Self {
            label: label.into(),
            configured: true,
            chunks,
            fail: false,
            calls: Mutex::new(0),
        }
    }

    /// An unconfigured generator (declines every call with a fail-soft frame).
    pub fn unconfigured(label: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            configured: false,
            chunks: Vec::new(),
            fail: false,
            calls: Mutex::new(0),
        }
    }

    /// A configured generator that errors on every call (for failover tests).
    pub fn failing(label: impl Into<String>) -> Self {
        Self {
            label: label.into(),
            configured: true,
            chunks: Vec::new(),
            fail: true,
            calls: Mutex::new(0),
        }
    }

    /// How many times `generate`/`stream` has been invoked.
    pub fn call_count(&self) -> u32 {
        *self.calls.lock().unwrap()
    }
}

impl ICloudChatGenerator for FakeCloudGenerator {
    fn generate(&self, _messages: &[CloudChatMessage]) -> Result<String, String> {
        *self.calls.lock().unwrap() += 1;
        if self.fail {
            return Err(format!("{} failed", self.label));
        }
        if !self.configured {
            return Ok(format!("[{} not configured]", self.label));
        }
        Ok(self.chunks.concat())
    }

    fn stream(&self, _messages: &[CloudChatMessage]) -> Result<Vec<String>, String> {
        *self.calls.lock().unwrap() += 1;
        if self.fail {
            return Err(format!("{} failed", self.label));
        }
        if !self.configured {
            return Ok(vec![format!("[{} not configured]", self.label)]);
        }
        Ok(self.chunks.clone())
    }
}

impl IConfigurableChatGenerator for FakeCloudGenerator {
    fn is_configured(&self) -> bool {
        self.configured
    }
    fn engine_label(&self) -> String {
        self.label.clone()
    }
    fn status_message(&self) -> String {
        if self.configured {
            format!("Ready · {}", self.label)
        } else {
            format!("{} API key not configured.", self.label)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// A generator entry that may carry configurability metadata.
// ─────────────────────────────────────────────────────────────────────────────

/// One generator slot in a chain. The optional `configured` view mirrors the C#
/// `g is IConfigurableChatGenerator c ? c.IsConfigured : true` test.
pub struct GeneratorEntry {
    gen: Box<dyn ICloudChatGenerator>,
    /// `None` → not an [`IConfigurableChatGenerator`] (presumed always ready).
    is_configured: Option<bool>,
    engine_label: Option<String>,
}

impl GeneratorEntry {
    /// A plain generator (presumed always ready).
    pub fn plain(gen: Box<dyn ICloudChatGenerator>) -> Self {
        Self {
            gen,
            is_configured: None,
            engine_label: None,
        }
    }

    /// A configurable generator (carries its `IsConfigured` + label).
    pub fn configurable<G: IConfigurableChatGenerator + 'static>(gen: G) -> Self {
        let is_configured = Some(gen.is_configured());
        let engine_label = Some(gen.engine_label());
        Self {
            gen: Box::new(gen),
            is_configured,
            engine_label,
        }
    }

    fn is_ready(&self) -> bool {
        self.is_configured.unwrap_or(true)
    }

    fn label(&self) -> String {
        self.engine_label.clone().unwrap_or_else(|| "generator".to_string())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CloudFallbackChain
// ─────────────────────────────────────────────────────────────────────────────

/// The frame the chain emits when nothing could serve the request. 1:1 with the
/// C# sentinel string.
pub const NO_GENERATOR_FRAME: &str =
    "[CloudFallbackChain: no configured generator could serve the request]";

/// (3.2.0) Tries an ordered list of generators and uses the first one *ready* to
/// serve a call. A generator that yields a fail-soft frame doesn't count as
/// ready — the chain skips it. Generators that error are also skipped. 1:1 with
/// the C# `CloudFallbackChain`.
pub struct CloudFallbackChain {
    generators: Vec<GeneratorEntry>,
}

impl CloudFallbackChain {
    /// Build a chain. Order matters — the first ready generator wins, so put
    /// on-device first for sovereign-by-default.
    pub fn new(generators: Vec<GeneratorEntry>) -> Self {
        Self { generators }
    }

    /// Number of generators in the chain.
    pub fn len(&self) -> usize {
        self.generators.len()
    }

    /// Whether the chain has no generators.
    pub fn is_empty(&self) -> bool {
        self.generators.is_empty()
    }

    /// Complete generation, falling through skipped/failed generators. 1:1 with
    /// the C# `GenerateAsync`.
    pub fn generate(&self, messages: &[CloudChatMessage]) -> String {
        for g in &self.generators {
            if !g.is_ready() {
                continue;
            }
            match g.gen.generate(messages) {
                Ok(text) => return text,
                Err(_) => continue, // fall through to the next generator
            }
        }
        NO_GENERATOR_FRAME.to_string()
    }

    /// Streamed generation. Commits to a generator only once it produces a real
    /// (non fail-soft) frame; otherwise moves on. 1:1 with the C# `StreamAsync`.
    pub fn stream(&self, messages: &[CloudChatMessage]) -> Vec<String> {
        for g in &self.generators {
            if !g.is_ready() {
                continue;
            }
            let chunks = match g.gen.stream(messages) {
                Ok(c) => c,
                Err(_) => continue, // faulted before producing anything
            };

            let mut yielded: Vec<String> = Vec::new();
            let mut declined = false;
            for chunk in chunks {
                if yielded.is_empty() && is_fail_soft_frame(&chunk) {
                    // Generator declined (e.g. no API key) — try the next.
                    declined = true;
                    break;
                }
                yielded.push(chunk);
            }
            if declined {
                continue;
            }
            if !yielded.is_empty() {
                return yielded;
            }
            // Empty stream (no frames at all) — fall through.
        }
        vec![NO_GENERATOR_FRAME.to_string()]
    }
}

/// A fail-soft frame starts with `[` and mentions "not configured" or
/// "CloudFallbackChain" (case-insensitive). 1:1 with the C# `IsFailSoftFrame`.
fn is_fail_soft_frame(chunk: &str) -> bool {
    chunk.starts_with('[')
        && (chunk.to_lowercase().contains("not configured")
            || chunk.to_lowercase().contains("cloudfallbackchain"))
}

// ─────────────────────────────────────────────────────────────────────────────
// BackupBrainOrchestrator
// ─────────────────────────────────────────────────────────────────────────────

/// (3.3.0) Health state of one brain in the chain. 1:1 with the C# `BrainHealth`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BrainHealth {
    /// Serving normally.
    Healthy,
    /// Tripped after N consecutive failures; out until the cool-down elapses.
    Degraded,
    /// Cool-down elapsed — half-open, ready for a retry.
    CoolingDown,
}

/// (3.3.0) Snapshot of brain health for monitoring. 1:1 with the C#
/// `BrainStatus`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrainStatus {
    pub label: String,
    pub health: BrainHealth,
    pub consecutive_failures: i32,
}

/// (3.3.0) Policy knobs. 1:1 with the C# `BackupBrainPolicy`.
#[derive(Debug, Clone, Copy)]
pub struct BackupBrainPolicy {
    /// Consecutive failures that push a brain to degraded (default 2).
    pub degraded_after_failures: i32,
    /// How long a degraded brain stays out before a retry (default 30 s).
    pub cool_down_duration: Duration,
    /// How many brains to try before giving up on one turn (default 3).
    pub max_retries_per_turn: i32,
}

impl Default for BackupBrainPolicy {
    fn default() -> Self {
        Self {
            degraded_after_failures: 2,
            cool_down_duration: Duration::seconds(30),
            max_retries_per_turn: 3,
        }
    }
}

/// A monotonic clock (mirrors the C# `Func<DateTimeOffset>`).
pub type Clock = Box<dyn Fn() -> DateTime<Utc> + Send + Sync>;

struct BrainEntry {
    gen: Box<dyn ICloudChatGenerator>,
    label: String,
    inner: Mutex<BrainEntryState>,
}

#[derive(Default)]
struct BrainEntryState {
    consecutive: i32,
    degraded_since: Option<DateTime<Utc>>,
    is_degraded: bool,
}

impl BrainEntry {
    fn health_at(&self, now: DateTime<Utc>, cool_down: Duration) -> BrainHealth {
        let s = self.inner.lock().unwrap();
        if !s.is_degraded {
            return BrainHealth::Healthy;
        }
        match s.degraded_since {
            Some(since) if now - since >= cool_down => BrainHealth::CoolingDown, // half-open
            _ => BrainHealth::Degraded,
        }
    }

    fn record_success(&self) {
        let mut s = self.inner.lock().unwrap();
        s.consecutive = 0;
        s.is_degraded = false;
    }

    fn record_failure(&self, threshold: i32, now: DateTime<Utc>) {
        let mut s = self.inner.lock().unwrap();
        s.consecutive += 1;
        if s.consecutive >= threshold {
            s.is_degraded = true;
            s.degraded_since = Some(now);
        }
    }
}

/// (3.3.0) Wraps an ordered set of brains; switches on failure and retries the
/// primary after a cool-down. Different from [`CloudFallbackChain`] (start-of-
/// call ordering) — this is between-turn failover. 1:1 with the C#
/// `BackupBrainOrchestrator`.
pub struct BackupBrainOrchestrator {
    brains: Vec<BrainEntry>,
    policy: BackupBrainPolicy,
    clock: Clock,
}

/// The frame emitted when every brain fails. 1:1 with the C# sentinel.
pub const ALL_BRAINS_FAILED_FRAME: &str = "[All brains failed.]";

impl BackupBrainOrchestrator {
    /// Constructs the orchestrator. Panics when `brains` is empty (mirrors the
    /// C# `ArgumentException`). `clock` defaults to [`Utc::now`].
    pub fn new(
        brains: Vec<(String, Box<dyn ICloudChatGenerator>)>,
        policy: Option<BackupBrainPolicy>,
        clock: Option<Clock>,
    ) -> Self {
        assert!(!brains.is_empty(), "At least one brain is required.");
        let brains = brains
            .into_iter()
            .map(|(label, gen)| BrainEntry {
                gen,
                label,
                inner: Mutex::new(BrainEntryState::default()),
            })
            .collect();
        Self {
            brains,
            policy: policy.unwrap_or_default(),
            clock: clock.unwrap_or_else(|| Box::new(Utc::now)),
        }
    }

    /// Snapshot of every brain's health. 1:1 with the C# `Statuses`.
    pub fn statuses(&self) -> Vec<BrainStatus> {
        let now = (self.clock)();
        self.brains
            .iter()
            .map(|e| {
                let health = e.health_at(now, self.policy.cool_down_duration);
                let consecutive_failures = e.inner.lock().unwrap().consecutive;
                BrainStatus {
                    label: e.label.clone(),
                    health,
                    consecutive_failures,
                }
            })
            .collect()
    }

    /// Generate, switching to the next available brain on failure, up to the
    /// per-turn retry budget. 1:1 with the C# `GenerateAsync`.
    pub fn generate(&self, messages: &[CloudChatMessage]) -> String {
        let max_retries = self.policy.max_retries_per_turn.min(self.brains.len() as i32);
        let mut tried: Vec<usize> = Vec::new();
        for _ in 0..max_retries {
            let Some(idx) = self.pick_available(&tried) else {
                break;
            };
            tried.push(idx);
            match self.brains[idx].gen.generate(messages) {
                Ok(result) => {
                    self.brains[idx].record_success();
                    return result;
                }
                Err(_) => {
                    self.brains[idx]
                        .record_failure(self.policy.degraded_after_failures, (self.clock)());
                }
            }
        }
        ALL_BRAINS_FAILED_FRAME.to_string()
    }

    /// Streamed variant. A brain that faults after emitting frames ends the turn
    /// (no restart mid-stream); one that faults before emitting anything is
    /// skipped. 1:1 with the C# `StreamAsync` semantics.
    pub fn stream(&self, messages: &[CloudChatMessage]) -> Vec<String> {
        let max_retries = self.policy.max_retries_per_turn.min(self.brains.len() as i32);
        let mut tried: Vec<usize> = Vec::new();
        for _ in 0..max_retries {
            let Some(idx) = self.pick_available(&tried) else {
                break;
            };
            tried.push(idx);

            match self.brains[idx].gen.stream(messages) {
                Ok(chunks) => {
                    if chunks.is_empty() {
                        // No frames — treat as unproductive; try the backup.
                        continue;
                    }
                    self.brains[idx].record_success();
                    return chunks;
                }
                Err(_) => {
                    // Init/immediate failure — record and try the next brain.
                    self.brains[idx]
                        .record_failure(self.policy.degraded_after_failures, (self.clock)());
                }
            }
        }
        vec![ALL_BRAINS_FAILED_FRAME.to_string()]
    }

    /// Picks the first healthy / cooling-down brain not in `skip`; failing that,
    /// the first untried brain (degraded ones might recover). 1:1 with the C#
    /// `PickAvailable`.
    fn pick_available(&self, skip: &[usize]) -> Option<usize> {
        let now = (self.clock)();
        for (i, e) in self.brains.iter().enumerate() {
            if skip.contains(&i) {
                continue;
            }
            let h = e.health_at(now, self.policy.cool_down_duration);
            if matches!(h, BrainHealth::Healthy | BrainHealth::CoolingDown) {
                return Some(i);
            }
        }
        // None healthy — pick the first untried brain anyway.
        (0..self.brains.len()).find(|i| !skip.contains(i))
    }
}
