//! service.rs
//!
//! The long-lived butler service contract + default implementation. Ported from
//! `IAIService.cs`, `AIService.cs`, and `FallbackAIService.cs`. The C# service
//! is async + owns a native `QwenTextGenerator`; the sync port owns any
//! object-safe [`IHostChatGenerator`] (a deterministic generator ships for
//! tests), and injects every external subsystem (tool bridge, episodic store,
//! persona, device context, observer, RAM probe) behind a trait so the runtime
//! stays in-memory.
//!
//! Faithful behaviours ported 1:1:
//!   * idempotent start / stop / prewarm / warm-up
//!   * system-prompt enrichment (persona hint + device context) — RAG/skill
//!     enrichment is delegated to injected hooks
//!   * episodic-memory write after each chat / stream / agentic run
//!   * the agentic loop with Qwen `<tool_call>` parsing ([`parse_tool_call`])
//!   * feedback submission with persona verbosity/formality adaptation
//!   * observer firing (error-isolated) for every lifecycle + inference event

use std::sync::{Arc, Mutex};
use std::time::{Duration as StdDuration, Instant};

use serde_json::Value;
use uuid::Uuid;

use crate::inference::{ChatMessage, GenerationOptions, IChatGenerator};
use crate::selector::ChatCapability;
use crate::memory::{
    EpisodicMemoryEntry, FeedbackAnalyser, FeedbackPolarity, FeedbackSignal, PersonaState,
};
use crate::models_v15::UpgradeInfo;
use crate::tools::{ToolInvocation, ToolResult};

use super::observer::{AIChatEvent, AIStreamEvent, AIToolEvent, BrownoutReason, IAIObserver};

/// Errors surfaced by the hosting service. Mirrors the C# exceptions
/// (`InvalidOperationException` / `ObjectDisposedException`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HostingError {
    /// The service is disposed.
    Disposed,
    /// The service is not ready / not started.
    NotReady(String),
    /// A generic failure with a message.
    Failed(String),
}

impl std::fmt::Display for HostingError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            HostingError::Disposed => f.write_str("AIService has been disposed."),
            HostingError::NotReady(m) => write!(f, "Butler is not ready: {m}"),
            HostingError::Failed(m) => write!(f, "{m}"),
        }
    }
}

impl std::error::Error for HostingError {}

// ─────────────────────────────────────────────────────────────────────────────
// IHostChatGenerator — object-safe generator contract for the hosting layer
// ─────────────────────────────────────────────────────────────────────────────

/// Object-safe chat generator used across the hosting layer (service +
/// cloud-fallback). The associated-type [`IChatGenerator`] can't be boxed, so
/// this erases the error to a `String`. A blanket impl adapts any
/// [`IChatGenerator`].
pub trait IHostChatGenerator: Send + Sync {
    /// Generate a complete reply.
    fn generate(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, String>;

    /// Stream the reply as content chunks (already-materialised, deterministic).
    fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, String>;
}

/// Blanket adapter: any associated-type [`IChatGenerator`] with a `'static`
/// error is an [`IHostChatGenerator`].
impl<G> IHostChatGenerator for G
where
    G: IChatGenerator + Send + Sync,
    G::Error: 'static,
{
    fn generate(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, String> {
        IChatGenerator::generate(self, messages, options).map_err(|e| e.to_string())
    }

    fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, String> {
        let iter = IChatGenerator::stream(self, messages, options).map_err(|e| e.to_string())?;
        let mut chunks = Vec::new();
        for item in iter {
            chunks.push(item.map_err(|e| e.to_string())?);
        }
        Ok(chunks)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Injected host subsystems (object-safe traits with in-memory defaults)
// ─────────────────────────────────────────────────────────────────────────────

/// Object-safe tool bridge for the hosting layer. The C# `IToolBridge` invoke
/// path; the sync port takes the invocation and returns a [`ToolResult`].
pub trait IHostToolBridge: Send + Sync {
    fn invoke(&self, invocation: &ToolInvocation) -> ToolResult;
}

/// Sync persona store used by the service (load/save a [`PersonaState`]).
pub trait IHostPersonaStore: Send + Sync {
    fn load(&self, user_id: &str) -> PersonaState;
    fn save(&self, persona: &PersonaState);
}

/// Sync feedback store used by the service.
pub trait IHostFeedbackStore: Send + Sync {
    fn add(&self, signal: FeedbackSignal);
    fn get_recent(&self, count: usize) -> Vec<FeedbackSignal>;
}

/// Sync episodic store used by the service (append-only writes + recall).
pub trait IHostEpisodicStore: Send + Sync {
    fn add(&self, entry: EpisodicMemoryEntry);
}

/// Device-context snapshot injected into the enriched system prompt. Mirrors the
/// fields the C# `BuildEnrichedSystemPromptAsync` reads off `IDeviceContext`.
#[derive(Debug, Clone, Default)]
pub struct DeviceContext {
    pub local_time: Option<String>,
    pub time_zone_id: Option<String>,
    pub location_hint: Option<String>,
    /// 0.0–1.0 battery fraction.
    pub battery_level: Option<f32>,
    pub is_charging: Option<bool>,
    pub network_type: Option<String>,
    pub active_app_id: Option<String>,
}

impl DeviceContext {
    fn has_any(&self) -> bool {
        self.local_time.is_some()
            || self.location_hint.is_some()
            || self.battery_level.is_some()
            || self.network_type.is_some()
            || self.active_app_id.is_some()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AIOptions
// ─────────────────────────────────────────────────────────────────────────────

/// Host options for [`AIService`]. A trimmed, portable projection of the C#
/// `AIOptions` — the fields the in-memory sync service actually consumes.
pub struct AIOptions {
    /// The base system prompt.
    pub system_prompt: String,
    /// Whether to run a warm-up generation on start.
    pub warm_on_start: bool,
    /// Cap on agentic-loop iterations. `None` → default 5.
    pub agentic_max_iterations: Option<u32>,
    /// Persona user id.
    pub persona_user_id: String,
    /// How many recent feedback signals the analyser considers. Default 20.
    pub feedback_recent_window: usize,
    /// Default generation options.
    pub default_generation_options: GenerationOptions,

    // Injected subsystems (all optional).
    pub tool_bridge: Option<Box<dyn IHostToolBridge>>,
    pub persona_store: Option<Box<dyn IHostPersonaStore>>,
    pub feedback_store: Option<Box<dyn IHostFeedbackStore>>,
    pub episodic_memory: Option<Box<dyn IHostEpisodicStore>>,
    pub device_context: Option<DeviceContext>,
    pub observer: Option<Box<dyn IAIObserver>>,

    // Neuron — two-slot residency (opt-in). `router` None → single-slot, unchanged.
    pub router: Option<Box<dyn super::neuron::INeuronRouter>>,
    /// Capability -> specialist pick (the BestFit analog).
    pub neuron_selector:
        Option<Box<dyn Fn(ChatCapability) -> Option<super::neuron::SpecialistPick> + Send + Sync>>,
    /// Model id -> built specialist generator (the loader analog).
    pub specialist_builder:
        Option<Box<dyn Fn(&str) -> Option<Arc<dyn IHostChatGenerator>> + Send + Sync>>,
    /// Generalist model id — a best-fit that resolves to it stays on the generalist.
    pub generalist_model_id: Option<String>,
    /// Reserved footprint of the generalist floor (RAM gate).
    pub generalist_reserved_bytes: i64,
    /// Live RAM ceiling. None → no gate.
    pub ram_available: Option<Box<dyn Fn() -> i64 + Send + Sync>>,
}

impl Default for AIOptions {
    fn default() -> Self {
        Self {
            system_prompt: "You are B!, a helpful on-device assistant.".to_string(),
            warm_on_start: true,
            agentic_max_iterations: None,
            persona_user_id: "default".to_string(),
            feedback_recent_window: 20,
            default_generation_options: GenerationOptions::default(),
            tool_bridge: None,
            persona_store: None,
            feedback_store: None,
            episodic_memory: None,
            device_context: None,
            observer: None,
            router: None,
            neuron_selector: None,
            specialist_builder: None,
            generalist_model_id: None,
            generalist_reserved_bytes: 0,
            ram_available: None,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAIService
// ─────────────────────────────────────────────────────────────────────────────

/// Long-lived butler service. Owns the loaded chat generator and exposes
/// ask / chat / stream / tool / agentic entry points. 1:1 with the C#
/// `IAIService` (sync).
pub trait IAIService: Send + Sync {
    /// `true` once start has completed and the model is loaded.
    fn is_ready(&self) -> bool;

    /// Resolves the model, loads it, and (optionally) warms up. Idempotent.
    fn start(&self) -> Result<(), HostingError>;

    /// Releases the model handle and shuts the service down.
    fn stop(&self) -> Result<(), HostingError>;

    /// Single user question — the enriched system prompt is prepended.
    fn ask(&self, question: &str) -> Result<String, HostingError>;

    /// Complete assistant reply for the supplied conversation.
    fn chat(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError>;

    /// Streamed assistant reply (materialised chunk list in the sync port).
    fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, HostingError>;

    /// Route a tool invocation to the configured bridge.
    fn invoke_tool(&self, invocation: &ToolInvocation) -> Result<ToolResult, HostingError>;

    /// Agentic run: generate, detect tool calls, execute, re-prompt until a
    /// plain-text response or the iteration cap is reached.
    fn agentic_chat(
        &self,
        prompt: &str,
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError>;

    /// Record a [`FeedbackSignal`] against a past response.
    fn submit_feedback(&self, signal: FeedbackSignal) -> Result<(), HostingError>;

    /// Detected model upgrades. Default: empty.
    fn check_for_upgrades(&self) -> Result<Vec<UpgradeInfo>, HostingError> {
        Ok(Vec::new())
    }

    /// Pre-warm the loaded generator. Default: [`start`](Self::start).
    fn prewarm(&self) -> Result<(), HostingError> {
        self.start()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AIService
// ─────────────────────────────────────────────────────────────────────────────

/// Default [`IAIService`]. Holds one [`IHostChatGenerator`] for the process
/// lifetime so the model isn't reloaded per call. 1:1 with the C# `AIService`
/// (in-memory, sync).
pub struct AIService {
    options: AIOptions,
    generator: Box<dyn IHostChatGenerator>,
    state: Mutex<ServiceState>,
    persona: Mutex<Option<PersonaState>>,
    slots: super::neuron::ResidentSlotManager,
}

#[derive(Default)]
struct ServiceState {
    started: bool,
    disposed: bool,
}

const TOOL_CALL_OPEN: &str = "<tool_call>";
const TOOL_CALL_CLOSE: &str = "</tool_call>";

impl AIService {
    /// Constructs the service over the given generator + options.
    pub fn new(mut options: AIOptions, generator: Box<dyn IHostChatGenerator>) -> Self {
        let reserved = options.generalist_reserved_bytes;
        let ram_available: Box<dyn Fn() -> i64 + Send + Sync> =
            options.ram_available.take().unwrap_or_else(|| Box::new(|| i64::MAX));
        let slots = super::neuron::ResidentSlotManager::new(reserved, ram_available);
        Self {
            options,
            generator,
            state: Mutex::new(ServiceState::default()),
            persona: Mutex::new(None),
            slots,
        }
    }

    /// Neuron slot selection. `None` → the generalist (unchanged). With a router:
    /// route the turn and, on a specialist decision, best-fit + hot-load
    /// (admission-gated) a specialist. Any miss degrades to the generalist.
    fn select_slot(&self, user_query: &str, has_image: bool) -> Option<Arc<dyn IHostChatGenerator>> {
        let router = self.options.router.as_deref()?;
        let decision = router.route(&super::neuron::RouteContext {
            query: user_query.to_string(),
            has_image,
        });
        if decision.organ != super::neuron::Organ::Specialist {
            return None;
        }
        let selector = self.options.neuron_selector.as_deref()?;
        let builder = self.options.specialist_builder.as_deref()?;
        let pick = selector(decision.capability)?;
        if let Some(gid) = &self.options.generalist_model_id {
            if pick.model_id.eq_ignore_ascii_case(gid) {
                return None; // best-fit resolved to the generalist itself
            }
        }
        self.slots.ensure_specialist(&pick, builder).generator
    }

    fn throw_if_disposed(&self) -> Result<(), HostingError> {
        if self.state.lock().unwrap().disposed {
            Err(HostingError::Disposed)
        } else {
            Ok(())
        }
    }

    fn ensure_started(&self) -> Result<(), HostingError> {
        self.throw_if_disposed()?;
        if self.state.lock().unwrap().started {
            return Ok(());
        }
        self.start()
    }

    fn fire<F: FnOnce(&dyn IAIObserver)>(&self, action: F) {
        if let Some(obs) = self.options.observer.as_deref() {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| action(obs)));
        }
    }

    fn ensure_persona(&self) -> PersonaState {
        let mut cache = self.persona.lock().unwrap();
        if let Some(p) = cache.as_ref() {
            return p.clone();
        }
        let persona = match self.options.persona_store.as_deref() {
            Some(store) => store.load(&self.options.persona_user_id),
            None => PersonaState::new(&self.options.persona_user_id),
        };
        *cache = Some(persona.clone());
        persona
    }

    fn set_persona(&self, persona: PersonaState) {
        *self.persona.lock().unwrap() = Some(persona);
    }

    fn try_save_persona(&self) {
        let cache = self.persona.lock().unwrap();
        if let (Some(p), Some(store)) = (cache.as_ref(), self.options.persona_store.as_deref()) {
            store.save(p);
        }
    }

    /// Builds the enriched system prompt: base + persona hint + device context.
    /// Mirrors the C# `BuildEnrichedSystemPromptAsync` (RAG/skill enrichment is
    /// delegated to injected hooks and omitted here).
    fn build_enriched_system_prompt(&self, _user_query: &str) -> String {
        let mut sb = self.options.system_prompt.clone();

        // 1. Persona hints.
        let hint = self.ensure_persona().to_system_prompt_hint();
        if !hint.trim().is_empty() {
            sb.push('\n');
            sb.push_str(&hint);
        }

        // 2. Device context.
        if let Some(ctx) = self.options.device_context.as_ref() {
            if ctx.has_any() {
                let mut ctx_lines: Vec<String> = Vec::new();
                if let Some(lt) = &ctx.local_time {
                    let tz = ctx.time_zone_id.clone().unwrap_or_else(|| "UTC".to_string());
                    ctx_lines.push(format!("Local time: {lt} ({tz})"));
                }
                if let Some(loc) = &ctx.location_hint {
                    if !loc.trim().is_empty() {
                        ctx_lines.push(format!("Location: {loc}"));
                    }
                }
                if let Some(b) = ctx.battery_level {
                    let pct = (b * 100.0) as i32;
                    let charging = if ctx.is_charging == Some(true) {
                        " (charging)"
                    } else {
                        ""
                    };
                    ctx_lines.push(format!("Battery: {pct}%{charging}"));
                }
                if let Some(nt) = &ctx.network_type {
                    if !nt.trim().is_empty() {
                        ctx_lines.push(format!("Network: {nt}"));
                    }
                }
                if let Some(app) = &ctx.active_app_id {
                    if !app.trim().is_empty() {
                        ctx_lines.push(format!("Active app: {app}"));
                    }
                }

                if !ctx_lines.is_empty() {
                    sb.push('\n');
                    sb.push_str("[Device context]\n");
                    for line in ctx_lines {
                        sb.push_str(&line);
                        sb.push('\n');
                    }
                }
            }
        }

        sb
    }

    /// Prepends the enriched system prompt unless the caller supplied their own
    /// system message. Mirrors `PrepareMessagesAsync`.
    fn prepare_messages(&self, messages: &[ChatMessage], user_query: &str) -> Vec<ChatMessage> {
        let system_content = self.build_enriched_system_prompt(user_query);
        let has_system = messages
            .iter()
            .any(|m| m.role.eq_ignore_ascii_case("system"));

        let mut prepared = Vec::with_capacity(messages.len() + 1);
        if has_system {
            prepared.extend_from_slice(messages);
        } else {
            if !system_content.trim().is_empty() {
                prepared.push(ChatMessage::system(system_content));
            }
            prepared.extend_from_slice(messages);
        }
        prepared
    }

    fn try_store_episode(&self, user_text: &str, assistant_text: &str) {
        let Some(store) = self.options.episodic_memory.as_deref() else {
            return;
        };
        if user_text.trim().is_empty() {
            return;
        }
        let mut entry = EpisodicMemoryEntry::new(user_text, assistant_text);
        entry.app_context = self
            .options
            .device_context
            .as_ref()
            .and_then(|c| c.active_app_id.clone());
        store.add(entry);
    }

    fn last_user_query(messages: &[ChatMessage]) -> String {
        messages
            .iter()
            .rev()
            .find(|m| m.role.eq_ignore_ascii_case("user"))
            .map(|m| m.content.clone())
            .unwrap_or_default()
    }

    fn warm_up(&self) {
        let warm_messages = vec![
            ChatMessage::system(self.options.system_prompt.clone()),
            ChatMessage::user("."),
        ];
        let warm_options = GenerationOptions {
            max_tokens: 1,
            temperature: 0.0,
            ..GenerationOptions::default()
        };
        let _ = self.generator.generate(&warm_messages, Some(&warm_options));
    }

    /// Attempts to parse a tool call from Qwen3's native
    /// `<tool_call>…</tool_call>` format. Returns `None` when absent. 1:1 with
    /// the C# `ParseToolCall`.
    pub fn parse_tool_call(response: &str) -> Option<ToolInvocation> {
        if response.trim().is_empty() {
            return None;
        }
        let start = response.find(TOOL_CALL_OPEN)?;
        let content_start = start + TOOL_CALL_OPEN.len();
        let end_rel = response[content_start..].find(TOOL_CALL_CLOSE)?;
        let json = response[content_start..content_start + end_rel].trim();
        if json.is_empty() {
            return None;
        }

        let root: Value = serde_json::from_str(json).ok()?;
        let obj = root.as_object()?;

        // Support both {"name":...} and {"tool_name":...}.
        let tool_name = obj
            .get("name")
            .and_then(|v| v.as_str())
            .or_else(|| obj.get("tool_name").and_then(|v| v.as_str()))?;
        if tool_name.trim().is_empty() {
            return None;
        }

        let mut args: std::collections::HashMap<String, Value> = std::collections::HashMap::new();
        if let Some(args_obj) = obj.get("arguments").and_then(|v| v.as_object()) {
            for (name, value) in args_obj {
                // C#: string values pass through; others keep raw text. Here we
                // preserve the JSON value (a strict superset that round-trips).
                args.insert(name.clone(), value.clone());
            }
        }

        Some(ToolInvocation::new(tool_name, args))
    }
}

impl IAIService for AIService {
    fn is_ready(&self) -> bool {
        let s = self.state.lock().unwrap();
        s.started && !s.disposed
    }

    fn start(&self) -> Result<(), HostingError> {
        self.throw_if_disposed()?;
        {
            let s = self.state.lock().unwrap();
            if s.started {
                return Ok(());
            }
        }

        if self.options.warm_on_start {
            self.warm_up();
        }

        self.state.lock().unwrap().started = true;
        self.fire(|o| o.on_started());
        Ok(())
    }

    fn stop(&self) -> Result<(), HostingError> {
        if self.state.lock().unwrap().disposed {
            return Ok(());
        }
        self.try_save_persona();
        self.slots.evict_specialist();
        {
            let mut s = self.state.lock().unwrap();
            s.started = false;
        }
        *self.persona.lock().unwrap() = None;
        self.fire(|o| o.on_stopped());
        Ok(())
    }

    fn ask(&self, question: &str) -> Result<String, HostingError> {
        if question.is_empty() {
            return Err(HostingError::Failed("question required".to_string()));
        }
        let messages = vec![ChatMessage::user(question)];
        let opts = self.options.default_generation_options.clone();
        self.chat(&messages, Some(&opts))
    }

    fn chat(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError> {
        self.ensure_started()?;

        let user_query = Self::last_user_query(messages);
        let prepared = self.prepare_messages(messages, &user_query);
        let default_opts = self.options.default_generation_options.clone();
        let effective = options.unwrap_or(&default_opts);

        let correlation_id = Uuid::new_v4();
        let start = Instant::now();
        // Neuron: generalist by default; a specialist may answer when a router is
        // configured. Byte-identical to the single-slot path when router is None.
        let slot = self.select_slot(&user_query, false);
        let response = match slot.as_ref() {
            Some(g) => g.generate(&prepared, Some(effective)),
            None => self.generator.generate(&prepared, Some(effective)),
        }
        .map_err(HostingError::Failed)?;
        let elapsed = start.elapsed();

        self.try_store_episode(&user_query, &response);

        let event = AIChatEvent {
            correlation_id,
            messages: prepared,
            response: response.clone(),
            elapsed,
            timestamp: chrono::Utc::now(),
        };
        self.fire(|o| o.on_chat_completed(&event));

        Ok(response)
    }

    fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, HostingError> {
        self.ensure_started()?;

        let user_query = Self::last_user_query(messages);
        let prepared = self.prepare_messages(messages, &user_query);
        let default_opts = self.options.default_generation_options.clone();
        let effective = options.unwrap_or(&default_opts);

        let correlation_id = Uuid::new_v4();
        let start = Instant::now();
        let slot = self.select_slot(&user_query, false);
        let chunks = match slot.as_ref() {
            Some(g) => g.stream(&prepared, Some(effective)),
            None => self.generator.stream(&prepared, Some(effective)),
        }
        .map_err(HostingError::Failed)?;

        if !chunks.is_empty() {
            let started = AIStreamEvent {
                correlation_id,
                messages: prepared.clone(),
                elapsed: start.elapsed(),
                token_count: 0,
                timestamp: chrono::Utc::now(),
            };
            self.fire(|o| o.on_stream_started(&started));
        }

        let full: String = chunks.concat();
        let token_count = chunks.len() as i32;
        let elapsed = start.elapsed();

        self.try_store_episode(&user_query, &full);

        let completed = AIStreamEvent {
            correlation_id,
            messages: prepared,
            elapsed,
            token_count,
            timestamp: chrono::Utc::now(),
        };
        self.fire(|o| o.on_stream_completed(&completed));

        Ok(chunks)
    }

    fn invoke_tool(&self, invocation: &ToolInvocation) -> Result<ToolResult, HostingError> {
        self.throw_if_disposed()?;

        let Some(bridge) = self.options.tool_bridge.as_deref() else {
            let fail = ToolResult::failure(&invocation.tool_name, "No tool bridge configured.");
            let event = AIToolEvent {
                correlation_id: Uuid::new_v4(),
                invocation: invocation.clone(),
                result: fail.clone(),
                elapsed: StdDuration::ZERO,
                timestamp: chrono::Utc::now(),
            };
            self.fire(|o| o.on_tool_invoked(&event));
            return Ok(fail);
        };

        let correlation_id = Uuid::new_v4();
        let start = Instant::now();
        let result = bridge.invoke(invocation);
        let elapsed = start.elapsed();

        let event = AIToolEvent {
            correlation_id,
            invocation: invocation.clone(),
            result: result.clone(),
            elapsed,
            timestamp: chrono::Utc::now(),
        };
        self.fire(|o| o.on_tool_invoked(&event));

        Ok(result)
    }

    fn agentic_chat(
        &self,
        prompt: &str,
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError> {
        if prompt.is_empty() {
            return Err(HostingError::Failed("prompt required".to_string()));
        }
        self.ensure_started()?;

        let max_iter = self.options.agentic_max_iterations.unwrap_or(5).max(1);
        let default_opts = self.options.default_generation_options.clone();
        let effective = options.unwrap_or(&default_opts).clone();

        let mut history = vec![ChatMessage::user(prompt)];
        let mut last_response = String::new();
        // Neuron slot selection for the whole agentic run (prompt has no image).
        let slot = self.select_slot(prompt, false);

        for _ in 0..max_iter {
            let prepared = self.prepare_messages(&history, prompt);
            let start = Instant::now();
            let response = match slot.as_ref() {
                Some(g) => g.generate(&prepared, Some(&effective)),
                None => self.generator.generate(&prepared, Some(&effective)),
            }
            .map_err(HostingError::Failed)?;
            let elapsed = start.elapsed();

            last_response = response.clone();
            history.push(ChatMessage::assistant(response.clone()));

            let event = AIChatEvent {
                correlation_id: Uuid::new_v4(),
                messages: prepared,
                response,
                elapsed,
                timestamp: chrono::Utc::now(),
            };
            self.fire(|o| o.on_chat_completed(&event));

            let Some(invocation) = Self::parse_tool_call(&last_response) else {
                break;
            };

            if self.options.tool_bridge.is_none() {
                history.push(ChatMessage::new(
                    "tool",
                    format!(
                        "{{\"tool\": \"{}\", \"error\": \"No tool bridge configured.\"}}",
                        invocation.tool_name
                    ),
                ));
                continue;
            }

            let tool_result = self.invoke_tool(&invocation)?;
            let tool_content = if tool_result.success {
                format!(
                    "{{\"tool\": \"{}\", \"result\": {}}}",
                    tool_result.tool_name,
                    serde_json::to_string(&tool_result.result).unwrap_or_else(|_| "null".into())
                )
            } else {
                format!(
                    "{{\"tool\": \"{}\", \"error\": {}}}",
                    tool_result.tool_name,
                    serde_json::to_string(&tool_result.error).unwrap_or_else(|_| "null".into())
                )
            };
            history.push(ChatMessage::new("tool", tool_content));
        }

        self.try_store_episode(prompt, &last_response);
        Ok(last_response)
    }

    fn submit_feedback(&self, signal: FeedbackSignal) -> Result<(), HostingError> {
        self.throw_if_disposed()?;

        let Some(store) = self.options.feedback_store.as_deref() else {
            return Ok(());
        };

        store.add(signal.clone());

        // Update in-memory persona from the signal.
        let mut persona = self.ensure_persona();
        match signal.polarity {
            FeedbackPolarity::Positive => persona.positive_signals += 1,
            FeedbackPolarity::Negative => persona.negative_signals += 1,
            FeedbackPolarity::Correction => {}
        }
        persona.total_interactions += 1;

        // Run the analyser over the recent window and apply adaptations.
        let recent = store.get_recent(self.options.feedback_recent_window);
        let adaptation = FeedbackAnalyser::default().analyse(recent);

        if adaptation.verbosity_delta < 0.0 {
            persona.verbosity = match persona.verbosity.as_str() {
                "detailed" => "balanced".to_string(),
                _ => "brief".to_string(),
            };
        } else if adaptation.verbosity_delta > 0.0 {
            persona.verbosity = match persona.verbosity.as_str() {
                "brief" => "balanced".to_string(),
                _ => "detailed".to_string(),
            };
        }

        if adaptation.formality_delta < 0.0 {
            persona.formality = match persona.formality.as_str() {
                "formal" => "neutral".to_string(),
                _ => "casual".to_string(),
            };
        } else if adaptation.formality_delta > 0.0 {
            persona.formality = match persona.formality.as_str() {
                "casual" => "neutral".to_string(),
                _ => "formal".to_string(),
            };
        }

        for topic in &adaptation.preferred_topics {
            let existing = persona.topic_weights.get(topic).copied().unwrap_or(0.0);
            persona.topic_weights.insert(topic.clone(), existing + 1.0);
        }

        self.set_persona(persona);
        self.try_save_persona();
        Ok(())
    }

    fn prewarm(&self) -> Result<(), HostingError> {
        self.throw_if_disposed()?;
        if !self.state.lock().unwrap().started {
            return self.start();
        }
        self.warm_up();
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FallbackAIService
// ─────────────────────────────────────────────────────────────────────────────

/// Probes available RAM (bytes). Injected so the fallback decision is
/// deterministic in tests. Mirrors the C# `GC.GetGCMemoryInfo()` read.
pub trait IRamProbe: Send + Sync {
    fn available_ram_bytes(&self) -> i64;
}

/// A fixed-value RAM probe for tests / headless scenarios.
#[derive(Debug, Clone, Copy)]
pub struct FixedRamProbe(pub i64);

impl IRamProbe for FixedRamProbe {
    fn available_ram_bytes(&self) -> i64 {
        self.0
    }
}

/// Wraps a local [`IAIService`] with a cloud fallback. Local inference is
/// preferred; cloud is used transparently when local is unavailable. 1:1 with
/// the C# `FallbackAIService`.
///
/// The C# takes a concrete `AIApiClient` cloud; the sync port accepts any
/// [`IAIService`] as the cloud so it can be an [`crate::hosting::AIApiClient`]
/// or a deterministic fake. Which backend is active is chosen at
/// [`start`](IAIService::start) time: local when available RAM ≥ threshold and
/// local start succeeds, else cloud.
pub struct FallbackAIService {
    local: Box<dyn IAIService>,
    cloud: Box<dyn IAIService>,
    ram_threshold_bytes: i64,
    ram_probe: Box<dyn IRamProbe>,
    active: Mutex<Active>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Active {
    None,
    Local,
    Cloud,
}

impl FallbackAIService {
    /// Default RAM threshold: 2 GiB (matches the C# default).
    pub const DEFAULT_RAM_THRESHOLD_BYTES: i64 = 2 * 1024 * 1024 * 1024;

    /// Constructs the fallback. `ram_threshold_bytes` `None` → 2 GiB.
    pub fn new(
        local: Box<dyn IAIService>,
        cloud: Box<dyn IAIService>,
        ram_threshold_bytes: Option<i64>,
        ram_probe: Box<dyn IRamProbe>,
    ) -> Self {
        Self {
            local,
            cloud,
            ram_threshold_bytes: ram_threshold_bytes.unwrap_or(Self::DEFAULT_RAM_THRESHOLD_BYTES),
            ram_probe,
            active: Mutex::new(Active::None),
        }
    }

    fn active_service(&self) -> Result<&dyn IAIService, HostingError> {
        match *self.active.lock().unwrap() {
            Active::Local => Ok(self.local.as_ref()),
            Active::Cloud => Ok(self.cloud.as_ref()),
            Active::None => Err(HostingError::NotReady(
                "FallbackAIService has not been started. Call start first.".to_string(),
            )),
        }
    }

    /// Whether the local backend is the active one (test/inspection helper).
    pub fn is_local_active(&self) -> bool {
        *self.active.lock().unwrap() == Active::Local
    }

    /// Whether the cloud backend is the active one (test/inspection helper).
    pub fn is_cloud_active(&self) -> bool {
        *self.active.lock().unwrap() == Active::Cloud
    }
}

impl IAIService for FallbackAIService {
    fn is_ready(&self) -> bool {
        match *self.active.lock().unwrap() {
            Active::Local => self.local.is_ready(),
            Active::Cloud => self.cloud.is_ready(),
            Active::None => false,
        }
    }

    fn start(&self) -> Result<(), HostingError> {
        let available = self.ram_probe.available_ram_bytes();
        if available >= self.ram_threshold_bytes {
            if self.local.start().is_ok() {
                *self.active.lock().unwrap() = Active::Local;
                return Ok(());
            }
            // Local start failed — fall through to cloud.
        }
        self.cloud.start()?;
        *self.active.lock().unwrap() = Active::Cloud;
        Ok(())
    }

    fn stop(&self) -> Result<(), HostingError> {
        match *self.active.lock().unwrap() {
            Active::Local => self.local.stop(),
            Active::Cloud => self.cloud.stop(),
            Active::None => Ok(()),
        }
    }

    fn ask(&self, question: &str) -> Result<String, HostingError> {
        self.active_service()?.ask(question)
    }

    fn chat(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError> {
        self.active_service()?.chat(messages, options)
    }

    fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, HostingError> {
        self.active_service()?.stream(messages, options)
    }

    fn invoke_tool(&self, invocation: &ToolInvocation) -> Result<ToolResult, HostingError> {
        self.active_service()?.invoke_tool(invocation)
    }

    fn agentic_chat(
        &self,
        prompt: &str,
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError> {
        self.active_service()?.agentic_chat(prompt, options)
    }

    fn submit_feedback(&self, signal: FeedbackSignal) -> Result<(), HostingError> {
        self.active_service()?.submit_feedback(signal)
    }
}

/// Suppress unused-import lint for [`BrownoutReason`] — it is part of the
/// ported public observer surface and re-exported, but the service body
/// references it only through the observer trait's default method.
const _: Option<BrownoutReason> = None;
