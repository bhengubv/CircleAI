//! neuron.rs
//!
//! The Neuron — concierge router + two-slot residency + host-neutral facade.
//! Port of `CircleAI.Hosting.Neuron`, adapted to the Rust sync hosting layer.
//!
//! The C# two-slot reuses `IModelSelector.BestFit` + `IModelLoader`; Rust's
//! sync hosting layer erases the generator behind `IHostChatGenerator` and its
//! selector API diverges, so the specialist path is expressed as two injected
//! closures: a `neuron_selector` (capability -> [`SpecialistPick`], the BestFit
//! analog) and a `specialist_builder` (model id -> generator, the loader analog).
//! The specialist slot holds an `Arc` so it can be cloned out of the lock.

use std::sync::{Arc, Mutex};

use crate::inference::ChatMessage;
use crate::selector::ChatCapability;

use super::chat_runtime::{ChatTurn, IChatRuntime, IPersistableChatRuntime};
use super::service::{HostingError, IAIService, IHostChatGenerator};

// ─────────────────────────────────────────────────────────────────────────────
// Concierge router
// ─────────────────────────────────────────────────────────────────────────────

/// Which organ answers a turn.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Organ {
    /// The always-warm generalist (the floor — never evicted).
    Generalist,
    /// A capability-matched specialist in the second slot.
    Specialist,
}

/// Inputs the concierge classifies for a single turn.
#[derive(Debug, Clone)]
pub struct RouteContext {
    pub query: String,
    pub has_image: bool,
}

/// The concierge's per-turn decision.
#[derive(Debug, Clone)]
pub struct RouteDecision {
    pub organ: Organ,
    pub capability: ChatCapability,
    pub reason: String,
}

impl RouteDecision {
    /// Route to the always-warm generalist.
    pub fn generalist(reason: impl Into<String>) -> Self {
        Self { organ: Organ::Generalist, capability: ChatCapability::DEFAULT, reason: reason.into() }
    }
    /// Route to a capability-matched specialist.
    pub fn specialist(capability: ChatCapability, reason: impl Into<String>) -> Self {
        Self { organ: Organ::Specialist, capability, reason: reason.into() }
    }
}

/// The concierge's decision layer. Mirrors `INeuronRouter`.
pub trait INeuronRouter: Send + Sync {
    fn route(&self, ctx: &RouteContext) -> RouteDecision;
}

/// Guardrail checkpoint. A `None` predicate applies no veto — the honest default.
pub struct NeuronGate {
    allow_specialist: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
}

impl NeuronGate {
    /// Builds a gate. `None` applies no veto.
    pub fn new(allow_specialist: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>) -> Self {
        Self { allow_specialist }
    }
    /// A gate that never vetoes.
    pub fn open() -> Self {
        Self { allow_specialist: None }
    }
    /// Apply the guardrail, returning the effective decision.
    pub fn apply(&self, decision: RouteDecision, ctx: &RouteContext) -> RouteDecision {
        if decision.organ == Organ::Specialist {
            if let Some(pred) = &self.allow_specialist {
                if !pred(&ctx.query) {
                    return RouteDecision::generalist("gate: specialist vetoed -> generalist");
                }
            }
        }
        decision
    }
}

const REASONING_CUES: &[&str] = &[
    "prove", "solve", "calculate", "derive", "algorithm", "complexity", "debug",
    "stack trace", "refactor", "regex", "step by step", "step-by-step", "theorem",
    "equation", "big-o", "big o",
];

/// Default router: modality (image -> vision), length (long -> long-context), and
/// reasoning cues (-> reasoning); else the generalist. Mirrors
/// `HeuristicNeuronRouter`.
pub struct HeuristicNeuronRouter {
    gate: NeuronGate,
    long_context_chars: usize,
}

impl HeuristicNeuronRouter {
    /// Builds the default router. `long_context_chars == 0` uses 4000.
    pub fn new(gate: NeuronGate, long_context_chars: usize) -> Self {
        Self {
            gate,
            long_context_chars: if long_context_chars == 0 { 4000 } else { long_context_chars },
        }
    }

    fn classify(&self, ctx: &RouteContext) -> RouteDecision {
        if ctx.has_image {
            return RouteDecision::specialist(ChatCapability::VISION, "image attached -> vision specialist");
        }
        if ctx.query.chars().count() >= self.long_context_chars {
            return RouteDecision::specialist(ChatCapability::LONG_CTX, "long prompt -> long-context specialist");
        }
        let lower = ctx.query.to_lowercase();
        for cue in REASONING_CUES {
            if lower.contains(cue) {
                return RouteDecision::specialist(ChatCapability::REASONING, "reasoning cue -> reasoning specialist");
            }
        }
        RouteDecision::generalist("no specialist cue -> generalist")
    }
}

impl INeuronRouter for HeuristicNeuronRouter {
    fn route(&self, ctx: &RouteContext) -> RouteDecision {
        self.gate.apply(self.classify(ctx), ctx)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Two-slot residency
// ─────────────────────────────────────────────────────────────────────────────

/// The BestFit analog: a capability resolved to a concrete specialist model +
/// its estimated footprint (for the RAM gate).
#[derive(Debug, Clone)]
pub struct SpecialistPick {
    pub model_id: String,
    pub estimated_bytes: i64,
}

/// Outcome of a specialist-slot admission attempt.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SlotOutcome {
    Admitted,
    AlreadyResident,
    InsufficientRam,
    BuildFailed,
}

/// Result of [`ResidentSlotManager::ensure_specialist`].
pub struct SlotAdmission {
    pub outcome: SlotOutcome,
    pub generator: Option<Arc<dyn IHostChatGenerator>>,
    pub message: String,
}

/// Manages one evictable specialist slot beside the always-warm generalist floor
/// (held by `AIService`). Only the generalist's reserved footprint counts against
/// the RAM gate. Mirrors `ResidentSlotManager`.
pub struct ResidentSlotManager {
    generalist_reserved_bytes: i64,
    ram_available: Box<dyn Fn() -> i64 + Send + Sync>,
    slot: Mutex<Option<(String, Arc<dyn IHostChatGenerator>)>>,
}

impl ResidentSlotManager {
    /// Builds a manager. `ram_available` supplies the live RAM ceiling.
    pub fn new(generalist_reserved_bytes: i64, ram_available: Box<dyn Fn() -> i64 + Send + Sync>) -> Self {
        Self {
            generalist_reserved_bytes: generalist_reserved_bytes.max(0),
            ram_available,
            slot: Mutex::new(None),
        }
    }

    /// The resident specialist's model id, or `None`.
    pub fn resident_specialist_model_id(&self) -> Option<String> {
        self.slot.lock().unwrap().as_ref().map(|(id, _)| id.clone())
    }

    /// Ensure a specialist for `pick` is resident, building it via `build` when
    /// needed. Admission gate: the generalist floor + the specialist footprint
    /// must fit under the RAM ceiling. On denial / build failure the slot is left
    /// empty and the caller answers from the generalist.
    pub fn ensure_specialist(
        &self,
        pick: &SpecialistPick,
        build: &dyn Fn(&str) -> Option<Arc<dyn IHostChatGenerator>>,
    ) -> SlotAdmission {
        let mut slot = self.slot.lock().unwrap();

        if let Some((id, gen)) = slot.as_ref() {
            if id.eq_ignore_ascii_case(&pick.model_id) {
                return SlotAdmission {
                    outcome: SlotOutcome::AlreadyResident,
                    generator: Some(gen.clone()),
                    message: format!("Specialist '{}' already resident.", pick.model_id),
                };
            }
        }

        let ceiling = (self.ram_available)().max(0);
        let needed = self.generalist_reserved_bytes + pick.estimated_bytes.max(0);
        if ceiling > 0 && needed > ceiling {
            return SlotAdmission {
                outcome: SlotOutcome::InsufficientRam,
                generator: None,
                message: format!(
                    "Specialist '{}' needs {} MiB; device ceiling {} MiB.",
                    pick.model_id,
                    needed >> 20,
                    ceiling >> 20
                ),
            };
        }

        // Only one specialist slot — evict the incumbent before building.
        *slot = None;

        match build(&pick.model_id) {
            Some(gen) => {
                *slot = Some((pick.model_id.clone(), gen.clone()));
                SlotAdmission {
                    outcome: SlotOutcome::Admitted,
                    generator: Some(gen),
                    message: format!("Specialist '{}' resident.", pick.model_id),
                }
            }
            None => SlotAdmission {
                outcome: SlotOutcome::BuildFailed,
                generator: None,
                message: format!("Specialist '{}' build failed.", pick.model_id),
            },
        }
    }

    /// Evict the specialist (the generalist floor is never touched).
    pub fn evict_specialist(&self) {
        *self.slot.lock().unwrap() = None;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NeuronNode facade
// ─────────────────────────────────────────────────────────────────────────────

/// Host-neutral `IChatRuntime` + `IPersistableChatRuntime` over an `IAIService`
/// brain. Streaming rides the brain's full pipeline (enrichment + concierge
/// routing + two-slot residency). Mirrors `NeuronNode`.
pub struct NeuronNode {
    brain: Arc<dyn IAIService>,
    id: String,
    snapshot_path: String,
}

impl NeuronNode {
    /// Builds the facade. `id` `None`/empty defaults to `"circleai-neuron"`.
    pub fn new(brain: Arc<dyn IAIService>, id: Option<String>, snapshot_path: Option<String>) -> Self {
        Self {
            brain,
            id: id
                .filter(|s| !s.trim().is_empty())
                .unwrap_or_else(|| "circleai-neuron".to_string()),
            snapshot_path: snapshot_path.unwrap_or_else(default_snapshot_path),
        }
    }

    /// The on-device brain. A companion session consumes it unchanged.
    pub fn brain(&self) -> &Arc<dyn IAIService> {
        &self.brain
    }
}

impl IChatRuntime for NeuronNode {
    fn id(&self) -> String {
        self.id.clone()
    }
    fn engine_label(&self) -> String {
        "CircleAI Neuron".to_string()
    }
    fn is_ready(&self) -> bool {
        self.brain.is_ready()
    }
    fn status_message(&self) -> String {
        if self.brain.is_ready() {
            "ready".to_string()
        } else {
            "loading model…".to_string()
        }
    }
    fn stream(&self, messages: &[ChatTurn]) -> Result<Vec<String>, HostingError> {
        let mapped: Vec<ChatMessage> = messages
            .iter()
            .map(|t| ChatMessage::new(t.role.clone(), t.content.clone()))
            .collect();
        self.brain.stream(&mapped, None)
    }
}

impl IPersistableChatRuntime for NeuronNode {
    fn session_snapshot_path(&self) -> Option<String> {
        Some(self.snapshot_path.clone())
    }
    // The sync hosting generator (`IHostChatGenerator`) is erased and carries no
    // KV snapshot, so session persistence is a documented no-op in this port —
    // the API seam is present for parity.
    fn save_session(&self, _path: &str) -> Result<bool, HostingError> {
        Ok(false)
    }
    fn load_session(&self, _path: &str) -> Result<bool, HostingError> {
        Ok(false)
    }
}

fn default_snapshot_path() -> String {
    let base = std::env::var("LOCALAPPDATA")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| std::env::temp_dir().to_string_lossy().to_string());
    format!("{base}/CircleAI/sessions/active.session")
}
