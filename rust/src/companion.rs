//! companion.rs
//!
//! InterfaceKind, CompanionContext, CompanionTurn, CompanionProactiveEvent,
//! and the ICompanionSession trait.
//!
//! The Companion is the HER + JARVIS persona — available on every surface,
//! with memory and identity that travels with the person.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// InterfaceKind
// ─────────────────────────────────────────────────────────────────────────────

/// The surface on which the Companion session is running.
///
/// Determines sensory capabilities, available UI affordances, and how the
/// Companion adapts its communication style.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum InterfaceKind {
    /// Mobile phone or tablet (MAUI).
    Mobile,
    /// Smartwatch or fitness band with a small display.
    Wearable,
    /// Desktop or laptop computer (MAUI or WPF).
    Desktop,
    /// Browser-based experience (Blazor).
    Web,
    /// Embedded IoT device — voice in, voice out, minimal compute.
    IoT,
    /// Always-on ambient surface — smart speaker, room display, car.
    Ambient,
    /// Programmatic / background / testing context (no UI).
    Headless,
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionContext
// ─────────────────────────────────────────────────────────────────────────────

/// Snapshot of all context injected into the Companion's system prompt.
///
/// Rebuilt at the start of each session and refreshed on request.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompanionContext {
    pub identity_id: String,
    pub display_name: String,
    pub preferred_language: Option<String>,
    pub interface: InterfaceKind,
    pub persona_hints: String,
    pub affect_summary: String,
    pub recent_memory_snippets: Vec<String>,
    pub active_goals: Vec<String>,
    pub context_built_at: DateTime<Utc>,
}

impl CompanionContext {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        identity_id: impl Into<String>,
        display_name: impl Into<String>,
        preferred_language: Option<String>,
        interface: InterfaceKind,
        persona_hints: impl Into<String>,
        affect_summary: impl Into<String>,
        recent_memory_snippets: Vec<String>,
        active_goals: Vec<String>,
    ) -> Self {
        Self {
            identity_id: identity_id.into(),
            display_name: display_name.into(),
            preferred_language,
            interface,
            persona_hints: persona_hints.into(),
            affect_summary: affect_summary.into(),
            recent_memory_snippets,
            active_goals,
            context_built_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionTurn
// ─────────────────────────────────────────────────────────────────────────────

/// A single turn in the Companion conversation log, held in memory for the
/// duration of the session.
///
/// `role` is one of `"user"` or `"assistant"`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompanionTurn {
    pub role: String,
    pub content: String,
    pub timestamp: DateTime<Utc>,
}

impl CompanionTurn {
    pub fn new(role: impl Into<String>, content: impl Into<String>) -> Self {
        Self {
            role: role.into(),
            content: content.into(),
            timestamp: Utc::now(),
        }
    }

    pub fn user(content: impl Into<String>) -> Self {
        Self::new("user", content)
    }

    pub fn assistant(content: impl Into<String>) -> Self {
        Self::new("assistant", content)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionProactiveEvent
// ─────────────────────────────────────────────────────────────────────────────

/// Metadata emitted when the Companion proactively initiates contact.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompanionProactiveEvent {
    pub session_id: String,
    pub identity_id: String,
    pub interface: InterfaceKind,
    pub message: String,
    pub trigger_name: String,
    pub generated_at: DateTime<Utc>,
}

impl CompanionProactiveEvent {
    pub fn new(
        session_id: impl Into<String>,
        identity_id: impl Into<String>,
        interface: InterfaceKind,
        message: impl Into<String>,
        trigger_name: impl Into<String>,
    ) -> Self {
        Self {
            session_id: session_id.into(),
            identity_id: identity_id.into(),
            interface,
            message: message.into(),
            trigger_name: trigger_name.into(),
            generated_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICompanionSession trait
// ─────────────────────────────────────────────────────────────────────────────

/// A Companion conversation session.
///
/// Combines identity awareness, cross-device memory, language adaptation,
/// affect sensing, and proactive reasoning into a single coherent interface.
///
/// NOTE: This is a synchronous trait shape for portable core definitions.
/// Platform implementations add async execution context. The `send`, `agent`,
/// and `refresh_context` methods map to the async equivalents in C#/Python.
/// The `stream` method returns a boxed iterator of string tokens.
pub trait ICompanionSession {
    type Error: std::error::Error;

    // ── Identity ──────────────────────────────────────────────────────────

    /// Stable unique identifier for this session.
    fn session_id(&self) -> &str;

    /// The authenticated identity driving this session.
    fn identity_id(&self) -> &str;

    /// The surface on which this session is running.
    fn interface(&self) -> InterfaceKind;

    // ── Core conversation ─────────────────────────────────────────────────

    /// Send a message to the Companion and receive a complete reply.
    fn send(&mut self, message: &str) -> Result<String, Self::Error>;

    /// Stream the Companion's reply token-by-token.
    fn stream(
        &mut self,
        message: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error>;

    /// Agentic mode: sends the instruction, detects tool calls in the reply,
    /// executes them, and re-prompts until the model produces a plain-text answer.
    fn agent(&mut self, instruction: &str) -> Result<String, Self::Error>;

    // ── Context ───────────────────────────────────────────────────────────

    /// Returns the most recent [`CompanionContext`] snapshot.
    fn get_context(&self) -> &CompanionContext;

    /// Refreshes the context from backing stores.
    fn refresh_context(&mut self) -> Result<(), Self::Error>;

    // ── History ───────────────────────────────────────────────────────────

    /// The in-session conversation history (this session only, not persisted).
    fn history(&self) -> &[CompanionTurn];

    // ── Feedback ──────────────────────────────────────────────────────────

    /// Signal satisfaction with the last reply.
    fn signal_feedback(&mut self, positive: bool, note: Option<&str>) -> Result<(), Self::Error>;
}
