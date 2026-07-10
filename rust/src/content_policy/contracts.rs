//! contracts.rs
//!
//! (2.6.0) Safety-guardrails contracts — Rust port of
//! `src/CircleAI.ContentPolicy/Contracts.cs`.
//!
//! The C# namespace is `CircleAI.ContentPolicy` (renamed from `CircleAI.Guardrails`
//! to avoid collision with the personal-safety domain pack `CircleAI.Safety`).
//! The `IFoo` interfaces are ported as `IFoo` traits (keeping the I-name) and the
//! C# `sealed record`s as plain immutable structs. All contracts are SYNC (matches
//! the existing sync trait style in this crate); the C# `ValueTask`/`CancellationToken`
//! shape collapses to direct returns.

use chrono::{DateTime, Utc};

/// (2.6.0) The verdict a content filter reaches for a piece of text.
///
/// Mirrors `enum SafetyVerdict { Allow, Flag, Refuse }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SafetyVerdict {
    /// Content is acceptable — let it through.
    Allow,
    /// Content is suspect — allow but surface for review / soft handling.
    Flag,
    /// Content violates policy — must be refused.
    Refuse,
}

/// (2.6.0) A single classification result from a content filter or
/// prompt-injection detector.
///
/// Mirrors `sealed record SafetyFinding(SafetyVerdict Verdict, string Category,
/// string Reason, float Confidence)`.
#[derive(Debug, Clone, PartialEq)]
pub struct SafetyFinding {
    pub verdict: SafetyVerdict,
    pub category: String,
    pub reason: String,
    pub confidence: f32,
}

impl SafetyFinding {
    /// Constructs a finding, mirroring the positional C# record constructor.
    pub fn new(
        verdict: SafetyVerdict,
        category: impl Into<String>,
        reason: impl Into<String>,
        confidence: f32,
    ) -> Self {
        Self {
            verdict,
            category: category.into(),
            reason: reason.into(),
            confidence,
        }
    }
}

/// (2.6.0) Per-token / per-message content filter.
///
/// Mirrors `interface IContentFilter`.
pub trait IContentFilter {
    /// Stable identifier for the backend implementation (e.g. `"keyword"`).
    fn backend_id(&self) -> &str;

    /// Classifies a piece of text, returning the resulting [`SafetyFinding`].
    fn classify(&self, text: &str) -> SafetyFinding;
}

/// (2.6.0) Refusal policy — decides whether a set of findings becomes a refusal.
///
/// Mirrors `interface IRefusalPolicy`.
pub trait IRefusalPolicy {
    /// Stable identifier for the backend implementation (e.g. `"threshold"`).
    fn backend_id(&self) -> &str;

    /// Returns `true` if the accumulated findings should result in a refusal.
    fn should_refuse(&self, findings: &[SafetyFinding]) -> bool;
}

/// (2.6.0) Prompt-injection detector — catches second-order attacks embedded in
/// untrusted content (RAG passages, web pages, tool output).
///
/// Mirrors `interface IPromptInjectionDetector`.
pub trait IPromptInjectionDetector {
    /// Stable identifier for the backend implementation (e.g. `"keyword"`).
    fn backend_id(&self) -> &str;

    /// Inspects untrusted content coming from `source_label` and returns a
    /// [`SafetyFinding`]. A `Refuse` verdict means an injection attempt was found.
    fn inspect(&self, untrusted_content: &str, source_label: &str) -> SafetyFinding;
}

/// (2.6.0) One entry in the append-only safety audit log.
///
/// Mirrors `sealed record SafetyAuditEntry(DateTimeOffset AtUtc, string UserId,
/// string Action, SafetyVerdict Verdict, string Reason)`.
#[derive(Debug, Clone, PartialEq)]
pub struct SafetyAuditEntry {
    pub at_utc: DateTime<Utc>,
    pub user_id: String,
    pub action: String,
    pub verdict: SafetyVerdict,
    pub reason: String,
}

impl SafetyAuditEntry {
    /// Constructs an entry, mirroring the positional C# record constructor.
    pub fn new(
        at_utc: DateTime<Utc>,
        user_id: impl Into<String>,
        action: impl Into<String>,
        verdict: SafetyVerdict,
        reason: impl Into<String>,
    ) -> Self {
        Self {
            at_utc,
            user_id: user_id.into(),
            action: action.into(),
            verdict,
            reason: reason.into(),
        }
    }
}

/// (2.6.0) Append-only safety audit log.
///
/// Mirrors `interface ISafetyAuditLog`. The default `limit` in the C# signature
/// (`int limit = 100`) is realised as an explicit argument; callers pass `100`
/// for the default.
pub trait ISafetyAuditLog {
    /// Stable identifier for the backend implementation.
    fn backend_id(&self) -> &str;

    /// Appends an entry to the log.
    fn log(&self, entry: SafetyAuditEntry);

    /// Reads up to `limit` most-recent entries, optionally filtered by `user_id`.
    fn read(&self, user_id: Option<&str>, limit: usize) -> Vec<SafetyAuditEntry>;
}
