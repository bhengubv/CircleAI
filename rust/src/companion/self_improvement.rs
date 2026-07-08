//! self_improvement.rs
//!
//! `SelfBenchSelfImprovementLoop` — the CircleAI.SelfBench-backed implementation
//! of the HerJarvis [`ISelfImprovementLoop`]. Ported 1:1 from
//! `SelfBenchSelfImprovementLoop.cs`: run the named suite against the current
//! model as baseline, obtain a candidate model, A/B-compare through a regression
//! gate, and only "apply" (promote) the candidate when the gate passes.
//!
//! The full CircleAI.SelfBench project is not part of this crate, so the small
//! set of SelfBench types this loop consumes is modelled here faithfully
//! ([`BenchTask`], [`BenchSummary`], [`RegressionGateConfig`], [`AbVerdict`]),
//! together with an [`IAbBenchRunner`] seam and a real in-memory
//! [`AbBenchRunner`] carrying the reference gate logic. The A/B runner, the bench
//! suite registry, the baseline/candidate model factories, and the promote hook
//! are all injected — a host wires the actual MNN/LoRA adapter management behind
//! them. Nothing here is a stub.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};

use super::her_jarvis::{ISelfImprovementLoop, SelfImprovementVerdict};

// ─────────────────────────────────────────────────────────────────────────────
// SelfBench contract shapes (subset used by the loop).
// ─────────────────────────────────────────────────────────────────────────────

/// One bench task. Only the fields the A/B gate reads are modelled: id and the
/// critical flag (regression on a critical task fails the gate).
#[derive(Debug, Clone, PartialEq)]
pub struct BenchTask {
    pub id: String,
    pub suite: String,
    pub prompt: String,
    pub expected: String,
    pub is_critical: bool,
}

impl BenchTask {
    pub fn new(
        id: impl Into<String>,
        suite: impl Into<String>,
        prompt: impl Into<String>,
        expected: impl Into<String>,
        is_critical: bool,
    ) -> Self {
        Self {
            id: id.into(),
            suite: suite.into(),
            prompt: prompt.into(),
            expected: expected.into(),
            is_critical,
        }
    }
}

/// Aggregate metrics across a bench run.
#[derive(Debug, Clone, PartialEq)]
pub struct BenchSummary {
    pub run_id: String,
    pub suite_id: String,
    pub task_count: usize,
    pub pass_count: usize,
    pub mean_score: f64,
    pub p50_latency_ms: f64,
    pub p95_latency_ms: f64,
    pub per_task_score: HashMap<String, f64>,
    pub completed_at_utc: DateTime<Utc>,
}

/// Regression-gate configuration. Mirrors the C# `RegressionGateConfig` defaults.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RegressionGateConfig {
    pub min_mean_score_improvement: f64,
    pub max_p95_latency_regression_ms: f64,
    pub max_critical_regressions: i32,
}

impl Default for RegressionGateConfig {
    fn default() -> Self {
        Self {
            min_mean_score_improvement: 0.01,
            max_p95_latency_regression_ms: 250.0,
            max_critical_regressions: 0,
        }
    }
}

/// The verdict of an A/B comparison.
#[derive(Debug, Clone, PartialEq)]
pub struct AbVerdict {
    pub should_promote: bool,
    pub baseline_summary: BenchSummary,
    pub candidate_summary: BenchSummary,
    pub mean_score_delta: f64,
    pub p95_latency_delta_ms: f64,
    pub critical_regressions: Vec<String>,
    pub reason: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// Injected seams: model, bench runner, suite registry, promote hook.
// ─────────────────────────────────────────────────────────────────────────────

/// A model under evaluation. The loop is agnostic to what it is — a host binds
/// the real `IAIService` (baseline vs freshly-LoRA'd candidate) behind this. The
/// [`AbBenchRunner`] scores tasks by calling [`IBenchModel::respond`].
pub trait IBenchModel: Send + Sync {
    /// A stable identifier for logging/telemetry.
    fn model_id(&self) -> String;
    /// Produces an answer for a bench prompt.
    fn respond(&self, prompt: &str) -> String;
}

/// Factory for a model instance (baseline or candidate), resolved per cycle.
pub type BenchModelFactory = Arc<dyn Fn() -> Arc<dyn IBenchModel> + Send + Sync>;

/// Promote hook — invoked with the winning verdict when the gate passes.
pub type PromoteFn = Arc<dyn Fn(&AbVerdict) + Send + Sync>;

/// Provides the tasks for a named suite.
pub trait IBenchSuiteRegistry: Send + Sync {
    /// Returns the tasks registered under `suite_id` (empty when unknown).
    fn get(&self, suite_id: &str) -> Vec<BenchTask>;
}

/// The A/B comparison seam — scores baseline vs candidate and applies the gate.
pub trait IAbBenchRunner: Send + Sync {
    /// Runs `tasks` against both models and returns a gated verdict.
    fn compare(
        &self,
        suite_id: &str,
        tasks: &[BenchTask],
        baseline: Arc<dyn IBenchModel>,
        candidate: Arc<dyn IBenchModel>,
        gate: RegressionGateConfig,
    ) -> AbVerdict;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory registry.
// ─────────────────────────────────────────────────────────────────────────────

/// A simple in-memory bench-suite registry.
#[derive(Debug, Default)]
pub struct InMemoryBenchSuiteRegistry {
    suites: Mutex<HashMap<String, Vec<BenchTask>>>,
}

impl InMemoryBenchSuiteRegistry {
    /// Returns an empty registry.
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) the tasks for a suite.
    pub fn register(&self, suite_id: &str, tasks: Vec<BenchTask>) {
        self.suites
            .lock()
            .unwrap()
            .insert(suite_id.to_string(), tasks);
    }
}

impl IBenchSuiteRegistry for InMemoryBenchSuiteRegistry {
    fn get(&self, suite_id: &str) -> Vec<BenchTask> {
        self.suites
            .lock()
            .unwrap()
            .get(suite_id)
            .cloned()
            .unwrap_or_default()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AbBenchRunner — real gate logic (port of AbBenchRunner.cs), exact-match scored.
// ─────────────────────────────────────────────────────────────────────────────

/// A/B bench runner. Scores each task by case-insensitive trimmed exact match of
/// the model's answer against the expected value, aggregates a per-model
/// [`BenchSummary`], and applies the regression gate exactly as the C#
/// `AbBenchRunner.CompareAsync` does (mean-improvement + p95-regression + no
/// critical regressions). Latency is not measured in-memory, so p95 delta is 0.
#[derive(Debug, Default)]
pub struct AbBenchRunner;

impl AbBenchRunner {
    /// Returns a new runner.
    pub fn new() -> Self {
        Self
    }

    fn run(
        &self,
        run_id: &str,
        suite_id: &str,
        tasks: &[BenchTask],
        model: &Arc<dyn IBenchModel>,
    ) -> BenchSummary {
        let mut per_task: HashMap<String, f64> = HashMap::new();
        let mut pass_count = 0usize;
        let mut score_sum = 0.0f64;
        for t in tasks {
            let actual = model.respond(&t.prompt);
            let score = if actual.trim().eq_ignore_ascii_case(t.expected.trim()) {
                1.0
            } else {
                0.0
            };
            if score >= 1.0 {
                pass_count += 1;
            }
            score_sum += score;
            per_task.insert(t.id.clone(), score);
        }
        let mean_score = if tasks.is_empty() {
            0.0
        } else {
            score_sum / tasks.len() as f64
        };
        BenchSummary {
            run_id: run_id.to_string(),
            suite_id: suite_id.to_string(),
            task_count: tasks.len(),
            pass_count,
            mean_score,
            p50_latency_ms: 0.0,
            p95_latency_ms: 0.0,
            per_task_score: per_task,
            completed_at_utc: Utc::now(),
        }
    }

    fn build_rejection_reason(
        mean_delta: f64,
        p95_delta: f64,
        criticals: &[String],
        gate: &RegressionGateConfig,
    ) -> String {
        let mut reasons: Vec<String> = Vec::new();
        if mean_delta < gate.min_mean_score_improvement {
            reasons.push(format!(
                "mean score \u{0394} {:.3} below threshold {:.3}",
                mean_delta, gate.min_mean_score_improvement
            ));
        }
        if p95_delta > gate.max_p95_latency_regression_ms {
            reasons.push(format!(
                "p95 latency regression {:.0}ms > {:.0}ms",
                p95_delta, gate.max_p95_latency_regression_ms
            ));
        }
        if criticals.len() as i32 > gate.max_critical_regressions {
            reasons.push(format!(
                "{} critical regressions: {}",
                criticals.len(),
                criticals.join(",")
            ));
        }
        if reasons.is_empty() {
            "rejected".to_string()
        } else {
            reasons.join("; ")
        }
    }
}

impl IAbBenchRunner for AbBenchRunner {
    fn compare(
        &self,
        suite_id: &str,
        tasks: &[BenchTask],
        baseline: Arc<dyn IBenchModel>,
        candidate: Arc<dyn IBenchModel>,
        gate: RegressionGateConfig,
    ) -> AbVerdict {
        let base_summary =
            self.run(&format!("{suite_id}@baseline"), suite_id, tasks, &baseline);
        let cand_summary =
            self.run(&format!("{suite_id}@candidate"), suite_id, tasks, &candidate);

        let mean_delta = cand_summary.mean_score - base_summary.mean_score;
        let p95_delta = cand_summary.p95_latency_ms - base_summary.p95_latency_ms;

        let mut critical_reg: Vec<String> = Vec::new();
        for crit in tasks.iter().filter(|t| t.is_critical) {
            let base_score = base_summary.per_task_score.get(&crit.id).copied().unwrap_or(0.0);
            let cand_score = cand_summary.per_task_score.get(&crit.id).copied().unwrap_or(0.0);
            if cand_score < base_score - 1e-9 {
                critical_reg.push(crit.id.clone());
            }
        }

        let promote = mean_delta >= gate.min_mean_score_improvement
            && p95_delta <= gate.max_p95_latency_regression_ms
            && (critical_reg.len() as i32) <= gate.max_critical_regressions;

        let reason = if promote {
            format!(
                "+{:.3} mean, p95 \u{0394} {:.0}ms, {} critical regressions",
                mean_delta,
                p95_delta,
                critical_reg.len()
            )
        } else {
            Self::build_rejection_reason(mean_delta, p95_delta, &critical_reg, &gate)
        };

        AbVerdict {
            should_promote: promote,
            baseline_summary: base_summary,
            candidate_summary: cand_summary,
            mean_score_delta: mean_delta,
            p95_latency_delta_ms: p95_delta,
            critical_regressions: critical_reg,
            reason,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SelfBenchSelfImprovementLoop — the ISelfImprovementLoop orchestration.
// ─────────────────────────────────────────────────────────────────────────────

/// SelfBench-backed self-improvement loop. Ported 1:1 from
/// `SelfBenchSelfImprovementLoop.cs`.
pub struct SelfBenchSelfImprovementLoop {
    registry: Arc<dyn IBenchSuiteRegistry>,
    runner: Arc<dyn IAbBenchRunner>,
    baseline_factory: BenchModelFactory,
    candidate_factory: BenchModelFactory,
    on_promote: PromoteFn,
    gate: RegressionGateConfig,
    best_scores: Mutex<HashMap<String, f64>>,
}

impl SelfBenchSelfImprovementLoop {
    /// Creates the loop. `on_promote` defaults to a no-op; `gate` to the
    /// reference defaults.
    pub fn new(
        registry: Arc<dyn IBenchSuiteRegistry>,
        runner: Arc<dyn IAbBenchRunner>,
        baseline_factory: BenchModelFactory,
        candidate_factory: BenchModelFactory,
        on_promote: Option<PromoteFn>,
        gate: Option<RegressionGateConfig>,
    ) -> Self {
        Self {
            registry,
            runner,
            baseline_factory,
            candidate_factory,
            on_promote: on_promote.unwrap_or_else(|| Arc::new(|_| {})),
            gate: gate.unwrap_or_default(),
            best_scores: Mutex::new(HashMap::new()),
        }
    }

    /// The best (promoted) score recorded for `suite_id` so far.
    pub fn best_score_for(&self, suite_id: &str) -> f64 {
        self.best_scores
            .lock()
            .unwrap()
            .get(suite_id)
            .copied()
            .unwrap_or(0.0)
    }
}

impl ISelfImprovementLoop for SelfBenchSelfImprovementLoop {
    fn cycle(&self, bench_suite_id: &str) -> SelfImprovementVerdict {
        // C#: blank suite id falls back to "default".
        let suite_id = if bench_suite_id.trim().is_empty() {
            "default"
        } else {
            bench_suite_id
        };
        let tasks = self.registry.get(suite_id);
        if tasks.is_empty() {
            return SelfImprovementVerdict::new("skipped: no tasks in suite", 0.0);
        }

        let baseline = (self.baseline_factory)();
        let candidate = (self.candidate_factory)();

        let verdict = self
            .runner
            .compare(suite_id, &tasks, baseline, candidate, self.gate);

        let new_score = verdict.candidate_summary.mean_score;
        let applied = if verdict.should_promote {
            (self.on_promote)(&verdict);
            let mut best = self.best_scores.lock().unwrap();
            let entry = best.entry(suite_id.to_string()).or_insert(new_score);
            *entry = entry.max(new_score);
            format!("promoted candidate ({})", verdict.reason)
        } else {
            format!("rejected ({})", verdict.reason)
        };
        SelfImprovementVerdict::new(applied, new_score)
    }
}
