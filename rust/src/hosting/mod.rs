//! hosting — CircleAI.Hosting runtime (Rust port).
//!
//! The long-lived butler service (`AIService`), its observer surface, the
//! scheduled-task engine, proactive reasoning, thermal/memory-pressure signals,
//! the tool catalog, generative-UI plug point, predictive warm-up, and the
//! transport endpoints. External / native / cloud subsystems are injected
//! behind object-safe traits with deterministic in-memory defaults, per the
//! no-real-IO porting brief. Everything is SYNC — the C# `Task`-returning
//! surface is projected to direct return values.
//!
//! Submodule map (C# → Rust):
//!   - `IAIObserver.cs` / `PushAIObserver.cs` / `AetherAIObserver.cs`
//!         → [`observer`] / [`push_observer`] / [`aether_observer`]
//!   - `IAIService.cs` / `AIService.cs` / `FallbackAIService.cs`
//!         → [`service`]
//!   - `AIApiClient.cs` / `IAIEndpoint.cs` / `Endpoints/*`
//!         → [`endpoints`]
//!   - `ScheduledAIService.cs` / `CronJobModels.cs` / `IScheduledTaskStore.cs`
//!     / `InMemoryScheduledTaskStore.cs` / `CronScheduleParser.cs`
//!         → [`scheduled_service`] / [`cron_models`] / [`scheduled_task_store`]
//!         / [`cron_schedule_parser`]
//!   - `ScheduleTrigger.cs` / `IdleTrigger.cs` / `ITriggerCondition.cs`
//!         → [`triggers`]
//!   - `IProactiveReasoningService.cs` / `ProactiveReasoningService.cs`
//!         → [`proactive_reasoning`]
//!   - `IThermalThrottleService.cs` / `ThermalThrottleService.cs`
//!         → [`thermal`]
//!   - `BackgroundInferenceWorker.cs` → [`background_worker`]
//!   - `IMemoryPressureSource.cs` → [`memory_pressure`]
//!   - `Warmup/*` → [`warmup`]
//!   - `Tools/*` → [`tool_catalog`]
//!   - `GenerativeUI/*` → [`generative_ui`]

use serde::{Deserialize, Serialize};

pub mod aether_observer;
pub mod background_worker;
pub mod cron_models;
pub mod cron_schedule_parser;
pub mod endpoints;
pub mod generative_ui;
pub mod memory_pressure;
pub mod observer;
pub mod proactive_reasoning;
pub mod push_observer;
pub mod scheduled_service;
pub mod scheduled_task_store;
pub mod service;
pub mod thermal;
pub mod tool_catalog;
pub mod triggers;
pub mod warmup;

// ─────────────────────────────────────────────────────────────────────────────
// Flat re-exports (so callers can write `crate::hosting::AIService` etc.)
// ─────────────────────────────────────────────────────────────────────────────

pub use aether_observer::{
    AetherAIObserver, ICircleAetherTransport, PublishedMessage, RecordingCircleAetherTransport,
};
pub use background_worker::BackgroundInferenceWorker;
pub use cron_models::{CronJob, CronJobState, DeliveryTarget};
pub use cron_schedule_parser::{CronScheduleError, CronScheduleParser};
pub use endpoints::{
    AIApiClient, AIHttpClient, HttpLoopbackEndpoint, HttpRequest, HttpResponse, IAIEndpoint,
    IButlerTransport, InProcessEndpoint, InProcessLoopbackTransport, RecordingButlerTransport,
};
pub use generative_ui::{
    IGenerativeUIRenderer, JsonRenderParser, RecordingGenerativeUIRenderer, RenderParseError,
    UiCatalogEntry, UiCatalogs, UiComponent,
};
pub use memory_pressure::{
    IMemoryPressureSource, ManualMemoryPressureSource, MemoryPressureLevel,
    NullMemoryPressureSource, PressureHandler, PressureSubscription,
};
pub use observer::{
    AIChatEvent, AIStreamEvent, AIToolEvent, BrownoutReason, IAIObserver, NullAIObserver,
};
pub use proactive_reasoning::{
    IAffectStore, IGoalStore, IProactiveReasoningService, InMemoryAffectStore, InMemoryGoalStore,
    ProactiveMessageEventArgs, ProactiveReasoningService,
};
pub use push_observer::{
    IPushNotificationSender, PushAIObserver, RecordingPushNotificationSender, SentPush,
};
pub use scheduled_service::{JobCompletedEventArgs, ScheduledAIService};
pub use scheduled_task_store::{IScheduledTaskStore, InMemoryScheduledTaskStore};
pub use service::{
    AIService, DeviceContext, FallbackAIService, FixedRamProbe, HostingError, IAIService,
    IHostChatGenerator, IHostEpisodicStore, IHostFeedbackStore, IHostPersonaStore, IHostToolBridge,
    IRamProbe,
};
pub use thermal::{
    classify_kelvin, classify_milli_celsius, IThermalSampler, IThermalThrottleService,
    ManualThermalSampler, ThermalState, ThermalStateHandler, ThermalThrottleService,
};
pub use tool_catalog::{
    import_from, IToolCatalog, IToolExecutor, IToolProvider, InMemoryToolCatalog, ToolDescriptor,
    ToolExecutionResult,
};
pub use triggers::{IdleTrigger, ITriggerCondition, ProactiveContext, ScheduleTrigger};
pub use warmup::{
    ArrivalForecast, HistogramRequestPredictor, IRequestPredictor, PredictiveWarmupController,
    PredictiveWarmupOptions,
};

// ─────────────────────────────────────────────────────────────────────────────
// HostAIOptions — full-surface options bag (mirrors CircleAI.Hosting.AIOptions)
// ─────────────────────────────────────────────────────────────────────────────

/// The full-surface options bag for the hosting layer. Mirrors the C#
/// `AIOptions` value-typed knobs (the injected subsystems live on
/// [`service::AIOptions`], which is the shape [`AIService`] actually consumes).
///
/// This type carries the loopback-endpoint configuration
/// ([`Self::loopback_port`] / [`Self::loopback_token`]) and the model / cloud
/// knobs that are host-visible even in the in-memory port.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct HostAIOptions {
    /// Logical model identifier. `None` → SDK best-fits from the device.
    pub model_id: Option<String>,
    /// Absolute path to a model file (bypasses the loader registry).
    pub model_path: Option<String>,
    /// System prompt prepended when a conversation carries no system message.
    pub system_prompt: String,
    /// Max context window in tokens. `None` → derive from device tier.
    pub context_size: Option<i32>,
    /// CPU threads for decode. `None` → inference-layer default.
    pub thread_count: Option<i32>,
    /// Run a 1-token warm-up generation on start (default `true`).
    pub warm_on_start: bool,
    /// Fire an upgrade check on start (default `false`).
    pub check_for_upgrades_on_start: bool,
    /// Directory where downloaded bundles live (for upgrade detection).
    pub model_storage_directory: Option<String>,
    /// RAG top-k injected per call (default 5; 0 disables).
    pub rag_top_k: i32,
    /// Persona user id (default `"default"`).
    pub persona_user_id: String,
    /// Max agentic loop iterations. `None` → derive from tier.
    pub agentic_max_iterations: Option<i32>,
    /// Loopback HTTP endpoint port (`0` → OS-assigned).
    pub loopback_port: u16,
    /// Loopback shared-secret token. `None` → random token at start.
    pub loopback_token: Option<String>,
    /// Cloud fallback enabled (default `false`).
    pub cloud_fallback_enabled: bool,
    /// Minimum RAM in bytes for local inference (default 2 GiB).
    pub cloud_fallback_ram_threshold_bytes: i64,
    /// Wi-Fi-only downloads (default `true`).
    pub wifi_only_model_download: bool,
    /// Skill top-k injected per call (default 5).
    pub skill_top_k: i32,
    /// Thermal pause enabled (default `true`).
    pub thermal_pause_enabled: bool,
}

impl Default for HostAIOptions {
    fn default() -> Self {
        Self {
            model_id: None,
            model_path: None,
            system_prompt: "You are B!, a helpful on-device assistant.".to_string(),
            context_size: None,
            thread_count: None,
            warm_on_start: true,
            check_for_upgrades_on_start: false,
            model_storage_directory: None,
            rag_top_k: 5,
            persona_user_id: "default".to_string(),
            agentic_max_iterations: None,
            loopback_port: 0,
            loopback_token: None,
            cloud_fallback_enabled: false,
            cloud_fallback_ram_threshold_bytes: 2i64 * 1024 * 1024 * 1024,
            wifi_only_model_download: true,
            skill_top_k: 5,
            thermal_pause_enabled: true,
        }
    }
}

impl HostAIOptions {
    /// Generates a cryptographically-random 32-byte token, base64-encoded.
    /// Mirrors the C# `AIOptions.GenerateRandomToken` (used by
    /// [`HttpLoopbackEndpoint`] when [`Self::loopback_token`] is `None`).
    pub fn generate_random_token() -> String {
        let bytes: [u8; 32] = uuid_random_bytes();
        base64_encode(&bytes)
    }
}

/// 32 random bytes drawn from two v4 UUIDs (the crate already depends on
/// `uuid` with a CSPRNG-backed v4 generator, so this needs no extra dep).
fn uuid_random_bytes() -> [u8; 32] {
    let a = *uuid::Uuid::new_v4().as_bytes();
    let b = *uuid::Uuid::new_v4().as_bytes();
    let mut out = [0u8; 32];
    out[..16].copy_from_slice(&a);
    out[16..].copy_from_slice(&b);
    out
}

/// Standard RFC 4648 base64 (with `+`/`/` and `=` padding) — matches
/// `Convert.ToBase64String`.
pub(crate) fn base64_encode(input: &[u8]) -> String {
    const TABLE: &[u8; 64] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    for chunk in input.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
        if chunk.len() > 1 {
            out.push(TABLE[((n >> 6) & 63) as usize] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(TABLE[(n & 63) as usize] as char);
        } else {
            out.push('=');
        }
    }
    out
}
