//! hosting_neuron_test.rs — the Rust Neuron port.
//!
//! Mirrors the C# CircleAI.Tests Neuron suite: the concierge decision table +
//! gate, the two-slot admission gate + eviction, the router-gated slot selection
//! inside AIService (specialist hot-load, generalist floor), and the NeuronNode
//! facade.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use circle_ai::hosting::chat_runtime::{
    ChatTurn, IChatRuntime, IPersistableChatRuntime, NullChatRuntime,
};
use circle_ai::hosting::neuron::{
    HeuristicNeuronRouter, INeuronRouter, NeuronGate, NeuronNode, Organ, ResidentSlotManager,
    RouteContext, RouteDecision, SlotOutcome, SpecialistPick,
};
use circle_ai::hosting::service::{AIOptions, AIService, IAIService, IHostChatGenerator};
use circle_ai::inference::{ChatMessage, GenerationOptions};
use circle_ai::selector::ChatCapability;

// ── test doubles ─────────────────────────────────────────────────────────────

struct FixedGen {
    reply: String,
}

impl IHostChatGenerator for FixedGen {
    fn generate(
        &self,
        _m: &[ChatMessage],
        _o: Option<&GenerationOptions>,
    ) -> Result<String, String> {
        Ok(self.reply.clone())
    }
    fn stream(
        &self,
        _m: &[ChatMessage],
        _o: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, String> {
        Ok(vec![self.reply.clone()])
    }
}

fn fixed(reply: &str) -> Box<dyn IHostChatGenerator> {
    Box::new(FixedGen { reply: reply.to_string() })
}

fn fixed_arc(reply: &str) -> Arc<dyn IHostChatGenerator> {
    Arc::new(FixedGen { reply: reply.to_string() })
}

struct FixedRouter {
    d: RouteDecision,
}

impl INeuronRouter for FixedRouter {
    fn route(&self, _ctx: &RouteContext) -> RouteDecision {
        self.d.clone()
    }
}

fn pick(id: &str, bytes: i64) -> SpecialistPick {
    SpecialistPick { model_id: id.to_string(), estimated_bytes: bytes }
}

// ── concierge router + gate ──────────────────────────────────────────────────

#[test]
fn router_plain_generalist() {
    let d = HeuristicNeuronRouter::new(NeuronGate::open(), 0)
        .route(&RouteContext { query: "what's the weather today?".into(), has_image: false });
    assert_eq!(d.organ, Organ::Generalist);
    assert_eq!(d.capability, ChatCapability::DEFAULT);
}

#[test]
fn router_vision() {
    let d = HeuristicNeuronRouter::new(NeuronGate::open(), 0)
        .route(&RouteContext { query: "what is this?".into(), has_image: true });
    assert_eq!(d.organ, Organ::Specialist);
    assert_eq!(d.capability, ChatCapability::VISION);
}

#[test]
fn router_reasoning() {
    let d = HeuristicNeuronRouter::new(NeuronGate::open(), 0)
        .route(&RouteContext { query: "please debug this stack trace".into(), has_image: false });
    assert_eq!(d.organ, Organ::Specialist);
    assert_eq!(d.capability, ChatCapability::REASONING);
}

#[test]
fn router_long_context() {
    let d = HeuristicNeuronRouter::new(NeuronGate::open(), 50)
        .route(&RouteContext { query: "x".repeat(60), has_image: false });
    assert_eq!(d.organ, Organ::Specialist);
    assert_eq!(d.capability, ChatCapability::LONG_CTX);
}

#[test]
fn router_gate_veto() {
    let gate = NeuronGate::new(Some(Box::new(|_q: &str| false)));
    let d = HeuristicNeuronRouter::new(gate, 0)
        .route(&RouteContext { query: "solve this equation".into(), has_image: false });
    assert_eq!(d.organ, Organ::Generalist);
}

// ── resident slot manager ────────────────────────────────────────────────────

#[test]
fn slot_admits_within_budget() {
    let m = ResidentSlotManager::new(1000, Box::new(|| 1_000_000i64));
    let g = fixed_arc("S");
    let build = |_id: &str| Some(g.clone());
    let a = m.ensure_specialist(&pick("spec", 5000), &build);
    assert_eq!(a.outcome, SlotOutcome::Admitted);
    assert!(a.generator.is_some());
    assert_eq!(m.resident_specialist_model_id().as_deref(), Some("spec"));
}

#[test]
fn slot_denies_over_budget() {
    let m = ResidentSlotManager::new(900_000, Box::new(|| 1_000_000i64));
    let build = |_id: &str| Some(fixed_arc("S"));
    let a = m.ensure_specialist(&pick("spec", 500_000), &build);
    assert_eq!(a.outcome, SlotOutcome::InsufficientRam);
    assert!(a.generator.is_none());
    assert_eq!(m.resident_specialist_model_id(), None);
}

#[test]
fn slot_already_resident() {
    let m = ResidentSlotManager::new(0, Box::new(|| 1_000_000i64));
    let builds = Arc::new(AtomicUsize::new(0));
    let b = builds.clone();
    let build = move |_id: &str| {
        b.fetch_add(1, Ordering::SeqCst);
        Some(fixed_arc("S"))
    };
    m.ensure_specialist(&pick("spec", 1), &build);
    let second = m.ensure_specialist(&pick("spec", 1), &build);
    assert_eq!(second.outcome, SlotOutcome::AlreadyResident);
    assert_eq!(builds.load(Ordering::SeqCst), 1);
}

#[test]
fn slot_swap_evicts() {
    let m = ResidentSlotManager::new(0, Box::new(|| 1_000_000i64));
    m.ensure_specialist(&pick("A", 1), &|_id: &str| Some(fixed_arc("A")));
    m.ensure_specialist(&pick("B", 1), &|_id: &str| Some(fixed_arc("B")));
    assert_eq!(m.resident_specialist_model_id().as_deref(), Some("B"));
}

#[test]
fn slot_build_failure() {
    let m = ResidentSlotManager::new(0, Box::new(|| 1_000_000i64));
    let a = m.ensure_specialist(&pick("spec", 1), &|_id: &str| None);
    assert_eq!(a.outcome, SlotOutcome::BuildFailed);
    assert_eq!(m.resident_specialist_model_id(), None);
}

#[test]
fn slot_evict() {
    let m = ResidentSlotManager::new(0, Box::new(|| 1_000_000i64));
    m.ensure_specialist(&pick("spec", 1), &|_id: &str| Some(fixed_arc("S")));
    m.evict_specialist();
    assert_eq!(m.resident_specialist_model_id(), None);
}

// ── AIService two-slot residency ─────────────────────────────────────────────

#[test]
fn aiservice_router_none_generalist() {
    let svc = AIService::new(AIOptions { warm_on_start: false, ..AIOptions::default() }, fixed("GEN"));
    svc.start().unwrap();
    assert_eq!(svc.ask("solve this equation").unwrap(), "GEN"); // reasoning cue, no router
}

#[test]
fn aiservice_hot_loads_specialist() {
    let opts = AIOptions {
        warm_on_start: false,
        router: Some(Box::new(FixedRouter {
            d: RouteDecision::specialist(ChatCapability::REASONING, "t"),
        })),
        neuron_selector: Some(Box::new(|_cap: ChatCapability| Some(pick("spec-model", 1024)))),
        specialist_builder: Some(Box::new(|_id: &str| Some(fixed_arc("SPEC")))),
        generalist_model_id: Some("gen-model".into()),
        ..AIOptions::default()
    };
    let svc = AIService::new(opts, fixed("GEN"));
    svc.start().unwrap();
    assert_eq!(svc.ask("anything").unwrap(), "SPEC");
}

#[test]
fn aiservice_best_fit_equals_generalist() {
    let opts = AIOptions {
        warm_on_start: false,
        router: Some(Box::new(FixedRouter {
            d: RouteDecision::specialist(ChatCapability::REASONING, "t"),
        })),
        neuron_selector: Some(Box::new(|_cap: ChatCapability| Some(pick("gen-model", 1024)))),
        specialist_builder: Some(Box::new(|_id: &str| Some(fixed_arc("SPEC")))),
        generalist_model_id: Some("gen-model".into()),
        ..AIOptions::default()
    };
    let svc = AIService::new(opts, fixed("GEN"));
    svc.start().unwrap();
    assert_eq!(svc.ask("anything").unwrap(), "GEN"); // best-fit == generalist
}

// ── NeuronNode facade + NullChatRuntime ──────────────────────────────────────

#[test]
fn neuron_node_over_brain() {
    let svc: Arc<dyn IAIService> = Arc::new(AIService::new(
        AIOptions { warm_on_start: false, ..AIOptions::default() },
        fixed("hello"),
    ));
    let node = NeuronNode::new(svc.clone(), None, None);

    assert_eq!(node.id(), "circleai-neuron");
    assert!(!node.is_ready());
    assert_eq!(node.status_message(), "loading model…");

    svc.start().unwrap();
    assert!(node.is_ready());
    assert_eq!(node.status_message(), "ready");

    let out = node.stream(&[ChatTurn::new("user", "hi")]).unwrap();
    assert_eq!(out.concat(), "hello");

    assert!(node.session_snapshot_path().is_some());
    // Sync port: session persistence is a documented no-op (false).
    assert!(!node.save_session("x").unwrap());
}

#[test]
fn null_runtime() {
    let null = NullChatRuntime;
    assert!(!null.is_ready());
    let out = null.stream(&[ChatTurn::new("user", "hi")]).unwrap();
    assert!(out.concat().contains("No chat engine"));
}
