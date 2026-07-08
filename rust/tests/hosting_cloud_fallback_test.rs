//! hosting_cloud_fallback_test.rs
//!
//! Verifies CloudFallbackChain (skip unconfigured / fail-soft, first ready
//! wins) and BackupBrainOrchestrator (degraded state machine, cool-down
//! half-open retry). Mirrors the C# CloudFallbackChain + BackupBrainOrchestrator.

use std::sync::{Arc, Mutex};

use chrono::{Duration, TimeZone, Utc};

use circle_ai::hosting_cloud_fallback::{
    BackupBrainOrchestrator, BackupBrainPolicy, BrainHealth, CloudChatMessage, CloudFallbackChain,
    FakeCloudGenerator, GeneratorEntry, ICloudChatGenerator, ALL_BRAINS_FAILED_FRAME,
    NO_GENERATOR_FRAME,
};

fn msg() -> Vec<CloudChatMessage> {
    vec![CloudChatMessage::new("user", "hi")]
}

#[test]
fn chain_uses_first_configured_generator() {
    let chain = CloudFallbackChain::new(vec![
        GeneratorEntry::configurable(FakeCloudGenerator::ready("primary", vec!["A".into()])),
        GeneratorEntry::configurable(FakeCloudGenerator::ready("backup", vec!["B".into()])),
    ]);
    assert_eq!(chain.generate(&msg()), "A");
    assert_eq!(chain.stream(&msg()), vec!["A".to_string()]);
}

#[test]
fn chain_skips_unconfigured_generator() {
    let chain = CloudFallbackChain::new(vec![
        GeneratorEntry::configurable(FakeCloudGenerator::unconfigured("no-key")),
        GeneratorEntry::configurable(FakeCloudGenerator::ready("backup", vec!["B".into()])),
    ]);
    // Unconfigured is skipped by the IsConfigured gate before it's even called.
    assert_eq!(chain.generate(&msg()), "B");
    assert_eq!(chain.stream(&msg()), vec!["B".to_string()]);
}

#[test]
fn chain_skips_fail_soft_frame_from_plain_generator() {
    // A *plain* (non-configurable) generator is presumed ready, but if it emits
    // a fail-soft frame the stream path moves on.
    let chain = CloudFallbackChain::new(vec![
        GeneratorEntry::plain(Box::new(FakeCloudGenerator::unconfigured("openai"))),
        GeneratorEntry::configurable(FakeCloudGenerator::ready("groq", vec!["real".into()])),
    ]);
    assert_eq!(chain.stream(&msg()), vec!["real".to_string()]);
}

#[test]
fn chain_returns_sentinel_when_nothing_serves() {
    let chain = CloudFallbackChain::new(vec![GeneratorEntry::configurable(
        FakeCloudGenerator::unconfigured("none"),
    )]);
    assert_eq!(chain.generate(&msg()), NO_GENERATOR_FRAME);
    assert_eq!(chain.stream(&msg()), vec![NO_GENERATOR_FRAME.to_string()]);
}

#[test]
fn chain_falls_through_erroring_generator() {
    let chain = CloudFallbackChain::new(vec![
        GeneratorEntry::configurable(FakeCloudGenerator::failing("flaky")),
        GeneratorEntry::configurable(FakeCloudGenerator::ready("solid", vec!["ok".into()])),
    ]);
    assert_eq!(chain.generate(&msg()), "ok");
}

// ── BackupBrainOrchestrator ─────────────────────────────────────────────────

fn brains(
    entries: Vec<(&str, Box<dyn ICloudChatGenerator>)>,
) -> Vec<(String, Box<dyn ICloudChatGenerator>)> {
    entries.into_iter().map(|(l, g)| (l.to_string(), g)).collect()
}

#[test]
fn orchestrator_uses_primary_when_healthy() {
    let orch = BackupBrainOrchestrator::new(
        brains(vec![
            ("primary", Box::new(FakeCloudGenerator::ready("primary", vec!["P".into()]))),
            ("backup", Box::new(FakeCloudGenerator::ready("backup", vec!["B".into()]))),
        ]),
        None,
        None,
    );
    assert_eq!(orch.generate(&msg()), "P");
    let statuses = orch.statuses();
    assert_eq!(statuses[0].health, BrainHealth::Healthy);
}

#[test]
fn orchestrator_fails_over_to_backup() {
    let orch = BackupBrainOrchestrator::new(
        brains(vec![
            ("primary", Box::new(FakeCloudGenerator::failing("primary"))),
            ("backup", Box::new(FakeCloudGenerator::ready("backup", vec!["B".into()]))),
        ]),
        None,
        None,
    );
    assert_eq!(orch.generate(&msg()), "B");
}

#[test]
fn orchestrator_all_fail_returns_sentinel() {
    let orch = BackupBrainOrchestrator::new(
        brains(vec![
            ("a", Box::new(FakeCloudGenerator::failing("a"))),
            ("b", Box::new(FakeCloudGenerator::failing("b"))),
        ]),
        None,
        None,
    );
    assert_eq!(orch.generate(&msg()), ALL_BRAINS_FAILED_FRAME);
}

#[test]
fn orchestrator_degrades_after_threshold_and_cools_down() {
    // Controllable clock so we can advance past the cool-down.
    let now = Arc::new(Mutex::new(Utc.with_ymd_and_hms(2026, 7, 8, 12, 0, 0).unwrap()));
    let clk = {
        let n = Arc::clone(&now);
        Box::new(move || *n.lock().unwrap())
    };
    let policy = BackupBrainPolicy {
        degraded_after_failures: 2,
        cool_down_duration: Duration::seconds(30),
        max_retries_per_turn: 1, // one attempt per turn so only the primary is tried
    };
    let orch = BackupBrainOrchestrator::new(
        brains(vec![(
            "primary",
            Box::new(FakeCloudGenerator::failing("primary")),
        )]),
        Some(policy),
        Some(clk),
    );

    // Two failing turns → primary degraded.
    assert_eq!(orch.generate(&msg()), ALL_BRAINS_FAILED_FRAME);
    assert_eq!(orch.generate(&msg()), ALL_BRAINS_FAILED_FRAME);
    assert_eq!(orch.statuses()[0].health, BrainHealth::Degraded);
    assert_eq!(orch.statuses()[0].consecutive_failures, 2);

    // Advance past the cool-down → half-open (CoolingDown).
    *now.lock().unwrap() = Utc.with_ymd_and_hms(2026, 7, 8, 12, 1, 0).unwrap();
    assert_eq!(orch.statuses()[0].health, BrainHealth::CoolingDown);
}

#[test]
fn orchestrator_success_resets_consecutive_failures() {
    let orch = BackupBrainOrchestrator::new(
        brains(vec![(
            "primary",
            Box::new(FakeCloudGenerator::ready("primary", vec!["ok".into()])),
        )]),
        None,
        None,
    );
    assert_eq!(orch.generate(&msg()), "ok");
    assert_eq!(orch.statuses()[0].consecutive_failures, 0);
    assert_eq!(orch.statuses()[0].health, BrainHealth::Healthy);
}

#[test]
#[should_panic(expected = "At least one brain is required")]
fn orchestrator_requires_at_least_one_brain() {
    let _ = BackupBrainOrchestrator::new(vec![], None, None);
}
