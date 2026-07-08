//! observer.rs
//!
//! Neutral observability hook. Ported from `IAIObserver.cs`. Consumers receive
//! lifecycle and inference events from the butler service WITHOUT any
//! Karma / Qi logic baked in. All methods have default no-op implementations so
//! partial observers are trivial to write. Observer exceptions are caught by
//! [`AIService`](crate::hosting::AIService) and swallowed; they never propagate
//! to the caller.
//!
//! The C# observer methods are async (`ValueTask`); the sync port returns unit.

use std::time::Duration;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::models::ChatMessage;
use crate::models_v15::UpgradeInfo;
use crate::tools::{ToolInvocation, ToolResult};

// ─────────────────────────────────────────────────────────────────────────────
// Event records
// ─────────────────────────────────────────────────────────────────────────────

/// Payload delivered to [`IAIObserver::on_chat_completed`]. Carries the full
/// conversation and the model's reply. 1:1 with the C# `AIChatEvent`.
#[derive(Debug, Clone)]
pub struct AIChatEvent {
    /// Per-call GUID for end-to-end tracing.
    pub correlation_id: Uuid,
    /// The input messages passed to the generator.
    pub messages: Vec<ChatMessage>,
    /// The complete response text.
    pub response: String,
    /// Wall-clock time from first token to last token.
    pub elapsed: Duration,
    /// UTC moment the call completed.
    pub timestamp: DateTime<Utc>,
}

/// Payload delivered to [`IAIObserver::on_stream_started`] and
/// [`IAIObserver::on_stream_completed`]. 1:1 with the C# `AIStreamEvent`.
#[derive(Debug, Clone)]
pub struct AIStreamEvent {
    /// Per-call GUID for end-to-end tracing.
    pub correlation_id: Uuid,
    /// The input messages passed to the generator.
    pub messages: Vec<ChatMessage>,
    /// `on_stream_started`: time-to-first-token. `on_stream_completed`: total.
    pub elapsed: Duration,
    /// `on_stream_started`: 0. `on_stream_completed`: number of tokens yielded.
    pub token_count: i32,
    /// UTC moment of the event.
    pub timestamp: DateTime<Utc>,
}

/// Payload delivered to [`IAIObserver::on_tool_invoked`]. 1:1 with the C#
/// `AIToolEvent`.
#[derive(Debug, Clone)]
pub struct AIToolEvent {
    /// Per-call GUID for end-to-end tracing.
    pub correlation_id: Uuid,
    /// The tool call that was dispatched.
    pub invocation: ToolInvocation,
    /// The result returned by the tool bridge.
    pub result: ToolResult,
    /// Wall-clock time for the tool call.
    pub elapsed: Duration,
    /// UTC moment the call completed.
    pub timestamp: DateTime<Utc>,
}

/// (RT-04) Why a brownout swap fired. Sized so future causes (thermal,
/// battery-floor) can be added without breaking ABI. 1:1 with the C#
/// `BrownoutReason`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum BrownoutReason {
    /// OS-reported memory pressure (Android onTrimMemory critical / iOS warning).
    MemoryPressure = 0,
    /// Battery dropped below the brownout floor — typically 10 %.
    BatteryFloor = 1,
    /// Thermal throttle declared the runtime must downshift.
    ThermalCritical = 2,
    /// Application requested the swap explicitly (e.g. test, manual toggle).
    Manual = 3,
}

// ─────────────────────────────────────────────────────────────────────────────
// Interface
// ─────────────────────────────────────────────────────────────────────────────

/// Observability hook for [`AIService`](crate::hosting::AIService). Receives
/// lifecycle and inference events. All methods are optional (default = no-op)
/// and must complete quickly. Implementations must be thread-safe.
pub trait IAIObserver: Send + Sync {
    /// Called once after the model has loaded and Butler is ready.
    fn on_started(&self) {}

    /// Called once when Butler is stopping / being disposed.
    fn on_stopped(&self) {}

    /// Called after a complete (non-streaming) chat response has been generated.
    fn on_chat_completed(&self, _event: &AIChatEvent) {}

    /// Called when a streaming response emits its first token
    /// (`token_count == 0`).
    fn on_stream_started(&self, _event: &AIStreamEvent) {}

    /// Called after a streaming response has finished (all tokens yielded, or
    /// cancelled). `token_count` holds the number emitted before completion.
    fn on_stream_completed(&self, _event: &AIStreamEvent) {}

    /// Called after a tool invocation has completed (success or failure).
    fn on_tool_invoked(&self, _event: &AIToolEvent) {}

    /// Called once when [`AIService`](crate::hosting::AIService) start has
    /// resolved which model to load. Fires before the fetch/load so observers
    /// can surface progress UI before bytes move.
    fn on_model_fetching(&self, _model_id: &str, _auto_selected: bool) {}

    /// Called when an upgrade check detects a model upgrade. Fires once per
    /// detected upgrade.
    fn on_upgrade_available(&self, _upgrade: &UpgradeInfo) {}

    /// (RT-04) Called when the runtime hot-swaps from one model in the fallback
    /// chain to the next under memory pressure.
    fn on_brownout(&self, _from: &str, _to: &str, _reason: BrownoutReason) {}
}

/// No-op observer baseline. Convenient default when a host wires no observer.
#[derive(Debug, Default)]
pub struct NullAIObserver;

impl IAIObserver for NullAIObserver {}
