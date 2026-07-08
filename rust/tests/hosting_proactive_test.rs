//! hosting_proactive_test.rs
//!
//! Verifies ProactiveReasoningService: first-firing trigger wins, prompt
//! wording (away-time + goals), handler dispatch, and the empty-trigger short
//! circuit. Also covers BackgroundInferenceWorker thermal pause. Mirrors the C#
//! ProactiveReasoningService + BackgroundInferenceWorker.

use std::sync::{Arc, Mutex};

use chrono::{Duration, TimeZone, Utc};

use circle_ai::hosting::background_worker::BackgroundInferenceWorker;
use circle_ai::hosting::proactive_reasoning::{
    IProactiveReasoningService, InMemoryAffectStore, InMemoryGoalStore, ProactiveMessageEventArgs,
    ProactiveReasoningService,
};
use circle_ai::hosting::service::{AIOptions, AIService, HostingError, IAIService, IHostChatGenerator};
use circle_ai::hosting::thermal::{ThermalState, ThermalThrottleService};
use circle_ai::hosting::triggers::{ITriggerCondition, IdleTrigger, ProactiveContext};
use circle_ai::inference::{ChatMessage, GenerationOptions};
use circle_ai::memory::{AffectState, Goal, GoalPriority};

struct PromptEchoGen;
impl IHostChatGenerator for PromptEchoGen {
    fn generate(&self, messages: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<String, String> {
        // Echo the last user content (the proactive prompt) so we can assert it.
        Ok(messages
            .iter()
            .rev()
            .find(|m| m.role == "user")
            .map(|m| m.content.clone())
            .unwrap_or_default())
    }
    fn stream(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<Vec<String>, String> {
        Ok(vec![])
    }
}

/// A trigger that always fires, with a fixed name.
struct AlwaysTrigger(&'static str);
impl ITriggerCondition for AlwaysTrigger {
    fn name(&self) -> &str {
        self.0
    }
    fn is_met(&self, _c: &ProactiveContext) -> bool {
        true
    }
}
struct NeverTrigger;
impl ITriggerCondition for NeverTrigger {
    fn name(&self) -> &str {
        "never"
    }
    fn is_met(&self, _c: &ProactiveContext) -> bool {
        false
    }
}

fn butler() -> AIService {
    AIService::new(
        AIOptions {
            warm_on_start: false,
            ..AIOptions::default()
        },
        Box::new(PromptEchoGen),
    )
}

#[test]
fn empty_triggers_returns_none() {
    let b = butler();
    let svc = ProactiveReasoningService::new(&b, None, None, vec![]);
    assert!(svc.check("user1", Utc::now()).is_none());
}

#[test]
fn first_firing_trigger_wins_and_dispatches_handler() {
    let b = butler();
    let captured: Arc<Mutex<Vec<ProactiveMessageEventArgs>>> = Arc::new(Mutex::new(Vec::new()));
    let cap2 = Arc::clone(&captured);
    let svc = ProactiveReasoningService::new(
        &b,
        None,
        None,
        vec![Box::new(NeverTrigger), Box::new(AlwaysTrigger("idle"))],
    );
    svc.on_proactive_message_ready(Box::new(move |args| cap2.lock().unwrap().push(args.clone())));

    let msg = svc.check("user1", Utc::now()).unwrap();
    // No affect/goals → the base prompt only.
    assert!(msg.starts_with("You are B!. Generate a brief, friendly check-in"));

    let events = captured.lock().unwrap();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].trigger_name, "idle");
    assert_eq!(events[0].user_id, "user1");
}

#[test]
fn prompt_includes_away_time_and_goals() {
    let b = butler();

    let now = Utc.with_ymd_and_hms(2026, 7, 8, 12, 0, 0).unwrap();
    let affect_store = InMemoryAffectStore::new();
    let mut affect = AffectState::default();
    affect.user_id = "u".to_string();
    // Last interaction 2 hours ago.
    affect.last_updated_at = now - Duration::hours(2);
    affect_store.set(affect);

    let goal_store = InMemoryGoalStore::new();
    let g = Goal::new("g1", "u", "Run a 5k", "Train for a 5k", GoalPriority::High);
    goal_store.add(g);

    let svc = ProactiveReasoningService::new(
        &b,
        Some(&goal_store),
        Some(&affect_store),
        vec![Box::new(AlwaysTrigger("idle"))],
    );
    let msg = svc.check("u", now).unwrap();
    assert!(msg.contains("The user has been away for approximately 2 hours."));
    assert!(msg.contains("They have 1 active goal: \"Run a 5k\"."));
}

#[test]
fn idle_trigger_respects_threshold() {
    let idle = IdleTrigger::new(Some(Duration::hours(4)));
    let ctx_short = ProactiveContext::new("u", Utc::now(), Duration::hours(1), None, vec![]);
    let ctx_long = ProactiveContext::new("u", Utc::now(), Duration::hours(5), None, vec![]);
    assert!(!idle.is_met(&ctx_short));
    assert!(idle.is_met(&ctx_long));
}

// ── BackgroundInferenceWorker ───────────────────────────────────────────────

struct NoopButler;
impl IAIService for NoopButler {
    fn is_ready(&self) -> bool {
        true
    }
    fn start(&self) -> Result<(), HostingError> {
        Ok(())
    }
    fn stop(&self) -> Result<(), HostingError> {
        Ok(())
    }
    fn ask(&self, _q: &str) -> Result<String, HostingError> {
        Ok("x".to_string())
    }
    fn chat(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<String, HostingError> {
        Ok(String::new())
    }
    fn stream(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<Vec<String>, HostingError> {
        Ok(vec![])
    }
    fn invoke_tool(
        &self,
        _i: &circle_ai::tools::ToolInvocation,
    ) -> Result<circle_ai::tools::ToolResult, HostingError> {
        Ok(circle_ai::tools::ToolResult::failure("x", "n/a"))
    }
    fn agentic_chat(&self, _p: &str, _o: Option<&GenerationOptions>) -> Result<String, HostingError> {
        Ok(String::new())
    }
    fn submit_feedback(&self, _s: circle_ai::memory::FeedbackSignal) -> Result<(), HostingError> {
        Ok(())
    }
}

/// A thermal sampler whose value the test can mutate after handing it to the
/// service (`Arc<Mutex<..>>` shared between the test and the service).
#[derive(Clone)]
struct SharedSampler(Arc<Mutex<ThermalState>>);
impl circle_ai::hosting::thermal::IThermalSampler for SharedSampler {
    fn sample(&self) -> ThermalState {
        *self.0.lock().unwrap()
    }
}

#[test]
fn worker_not_paused_when_thermal_normal() {
    let cell = Arc::new(Mutex::new(ThermalState::Normal));
    let thermal = Arc::new(ThermalThrottleService::new(SharedSampler(cell.clone())));
    let butler = NoopButler;
    let worker = BackgroundInferenceWorker::new(&butler, Some(thermal as _));
    worker.start().unwrap();
    assert!(!worker.is_paused());
    worker.stop().unwrap();
    // Double-stop is a no-op.
    worker.stop().unwrap();
}

#[test]
fn worker_flips_paused_on_thermal_transition() {
    let cell = Arc::new(Mutex::new(ThermalState::Normal));
    let thermal = Arc::new(ThermalThrottleService::new(SharedSampler(cell.clone())));
    let butler = NoopButler;
    let worker = BackgroundInferenceWorker::new(&butler, Some(Arc::clone(&thermal) as _));
    worker.start().unwrap();
    assert!(!worker.is_paused());

    // Heat up → poll → the worker's StateChanged handler fires and pauses.
    *cell.lock().unwrap() = ThermalState::Critical;
    thermal.poll_once();
    assert!(worker.is_paused());

    // Cool down → poll → resume.
    *cell.lock().unwrap() = ThermalState::Normal;
    thermal.poll_once();
    assert!(!worker.is_paused());
}
