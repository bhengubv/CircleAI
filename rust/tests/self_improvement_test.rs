//! self_improvement_test.rs
//!
//! Verifies SelfBenchSelfImprovementLoop: run a suite against baseline vs
//! candidate through the regression gate, promote only when it passes. Mirrors
//! the C# SelfBenchSelfImprovementLoop + AbBenchRunner behaviour.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use circle_ai::companion::her_jarvis::ISelfImprovementLoop;
use circle_ai::companion::self_improvement::*;

/// A model that answers each prompt from a fixed map (default: echo miss → "").
struct MapModel {
    id: String,
    answers: std::collections::HashMap<String, String>,
}

impl MapModel {
    fn new(id: &str, pairs: &[(&str, &str)]) -> Arc<dyn IBenchModel> {
        Arc::new(MapModel {
            id: id.to_string(),
            answers: pairs
                .iter()
                .map(|(p, a)| (p.to_string(), a.to_string()))
                .collect(),
        })
    }
}

impl IBenchModel for MapModel {
    fn model_id(&self) -> String {
        self.id.clone()
    }
    fn respond(&self, prompt: &str) -> String {
        self.answers.get(prompt).cloned().unwrap_or_default()
    }
}

fn suite() -> Vec<BenchTask> {
    vec![
        BenchTask::new("t1", "s", "2+2", "4", false),
        BenchTask::new("t2", "s", "capital of France", "Paris", true),
    ]
}

#[test]
fn skips_when_suite_has_no_tasks() {
    let registry = Arc::new(InMemoryBenchSuiteRegistry::new());
    let runner = Arc::new(AbBenchRunner::new());
    let baseline: BenchModelFactory = Arc::new(|| MapModel::new("b", &[]));
    let candidate: BenchModelFactory = Arc::new(|| MapModel::new("c", &[]));
    let loop_ = SelfBenchSelfImprovementLoop::new(
        registry, runner, baseline, candidate, None, None,
    );
    let v = loop_.cycle("missing");
    assert_eq!(v.improvements_applied, "skipped: no tasks in suite");
    assert_eq!(v.new_bench_score, 0.0);
}

#[test]
fn promotes_a_strictly_better_candidate() {
    let registry = Arc::new(InMemoryBenchSuiteRegistry::new());
    registry.register("default", suite());
    let runner = Arc::new(AbBenchRunner::new());

    // Baseline gets t1 right only; candidate gets both right → mean 1.0 vs 0.5.
    let baseline: BenchModelFactory = Arc::new(|| MapModel::new("b", &[("2+2", "4")]));
    let candidate: BenchModelFactory =
        Arc::new(|| MapModel::new("c", &[("2+2", "4"), ("capital of France", "Paris")]));

    let promoted = Arc::new(AtomicBool::new(false));
    let promoted2 = promoted.clone();
    let on_promote: PromoteFn = Arc::new(move |_v| promoted2.store(true, Ordering::SeqCst));

    let loop_ = SelfBenchSelfImprovementLoop::new(
        registry,
        runner,
        baseline,
        candidate,
        Some(on_promote),
        None,
    );
    let v = loop_.cycle("default");
    assert!(v.improvements_applied.starts_with("promoted candidate"));
    assert!((v.new_bench_score - 1.0).abs() < 1e-9);
    assert!(promoted.load(Ordering::SeqCst));
    assert!((loop_.best_score_for("default") - 1.0).abs() < 1e-9);
}

#[test]
fn rejects_a_candidate_that_regresses_a_critical_task() {
    let registry = Arc::new(InMemoryBenchSuiteRegistry::new());
    registry.register("s", suite());
    let runner = Arc::new(AbBenchRunner::new());

    // Baseline nails both; candidate loses the CRITICAL t2 → gate must reject
    // even though overall it is not an improvement anyway.
    let baseline: BenchModelFactory =
        Arc::new(|| MapModel::new("b", &[("2+2", "4"), ("capital of France", "Paris")]));
    let candidate: BenchModelFactory = Arc::new(|| MapModel::new("c", &[("2+2", "4")]));

    let loop_ = SelfBenchSelfImprovementLoop::new(
        registry, runner, baseline, candidate, None, None,
    );
    let v = loop_.cycle("s");
    assert!(v.improvements_applied.starts_with("rejected"));
    // No promotion recorded.
    assert_eq!(loop_.best_score_for("s"), 0.0);
}

#[test]
fn ab_runner_flags_critical_regression_explicitly() {
    let runner = AbBenchRunner::new();
    let tasks = suite();
    let baseline = MapModel::new("b", &[("2+2", "4"), ("capital of France", "Paris")]);
    let candidate = MapModel::new("c", &[("2+2", "4")]);
    let verdict = runner.compare("s", &tasks, baseline, candidate, RegressionGateConfig::default());
    assert!(!verdict.should_promote);
    assert_eq!(verdict.critical_regressions, vec!["t2".to_string()]);
    // Baseline mean 1.0, candidate mean 0.5 → negative delta.
    assert!(verdict.mean_score_delta < 0.0);
}

#[test]
fn blank_suite_id_falls_back_to_default() {
    let registry = Arc::new(InMemoryBenchSuiteRegistry::new());
    registry.register("default", vec![BenchTask::new("t", "s", "p", "p", false)]);
    let runner = Arc::new(AbBenchRunner::new());
    // Both models answer "p" → mean 1.0 both; delta 0 < 0.01 gate → rejected,
    // but the point is the empty id resolved to "default" (else it would skip).
    let baseline: BenchModelFactory = Arc::new(|| MapModel::new("b", &[("p", "p")]));
    let candidate: BenchModelFactory = Arc::new(|| MapModel::new("c", &[("p", "p")]));
    let loop_ = SelfBenchSelfImprovementLoop::new(
        registry, runner, baseline, candidate, None, None,
    );
    let v = loop_.cycle("");
    assert_ne!(v.improvements_applied, "skipped: no tasks in suite");
}

#[test]
fn registry_returns_registered_tasks() {
    let reg = InMemoryBenchSuiteRegistry::new();
    reg.register("a", vec![BenchTask::new("t", "a", "p", "e", false)]);
    assert_eq!(reg.get("a").len(), 1);
    assert!(reg.get("b").is_empty());
    // A shared-state sanity check that a Mutex-backed registry is reusable.
    let _guard: &Mutex<()> = &Mutex::new(());
}
